using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RegionChangeClicker {
public static class Detection {
    // Row-major first changed pixel, always inside the changed set.
    public static Point? Find(Bitmap before, Bitmap after, int tolerance) {
        if (before.Size != after.Size) throw new ArgumentException("Image sizes differ");
        Rectangle r = new Rectangle(Point.Empty, before.Size);
        BitmapData a = before.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData b = null;
        try {
            b = after.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] ar = new byte[before.Width * 4], br = new byte[ar.Length];
            for (int y = 0; y < before.Height; y++) {
                Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), ar, 0, ar.Length);
                Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), br, 0, br.Length);
                for (int x = 0; x < before.Width; x++)
                    for (int c = 0; c < 3; c++)
                        if (Math.Abs(ar[x*4+c] - br[x*4+c]) > tolerance) return new Point(x,y);
            }
            return null;
        } finally { before.UnlockBits(a); if (b != null) after.UnlockBits(b); }
    }
}
public sealed class OneShot : IDisposable {
    Bitmap baseline;
    public bool Active { get { return baseline != null; } }
    public void Start(Bitmap image) { Dispose(); baseline = (Bitmap)image.Clone(); }
    public bool Check(Bitmap image, int tolerance, Action<Point> click) {
        if (!Active) return false;
        Point? p = Detection.Find(baseline, image, tolerance);
        if (!p.HasValue) return false;
        Dispose(); // Disarm before dispatching input, including when dispatch fails.
        click(p.Value);
        return true;
    }
    public void Dispose() { if (baseline != null) { baseline.Dispose(); baseline = null; } }
}
public sealed class RepeatDetection : IDisposable {
    readonly OneShot detector=new OneShot();
    readonly Func<long> now;
    bool waiting;
    long resumeAt;
    int maximum, cooldown;
    public bool Active { get; private set; }
    public int Completed { get; private set; }
    public bool Waiting { get { return Active && waiting; } }
    public long RemainingMilliseconds { get { return Waiting ? Math.Max(0,resumeAt-now()) : 0; } }
    public RepeatDetection() {
        Stopwatch clock=Stopwatch.StartNew(); now=delegate { return clock.ElapsedMilliseconds; };
    }
    public RepeatDetection(Func<long> clock) { if(clock==null)throw new ArgumentNullException("clock"); now=clock; }
    public void Start(Bitmap image,int maximumClicks,int cooldownMilliseconds) {
        if(maximumClicks<1 || maximumClicks>10000)throw new ArgumentOutOfRangeException("maximumClicks");
        if(cooldownMilliseconds<0 || cooldownMilliseconds>600000)throw new ArgumentOutOfRangeException("cooldownMilliseconds");
        Dispose(); maximum=maximumClicks; cooldown=cooldownMilliseconds; Completed=0;
        detector.Start(image); Active=true;
    }
    public bool Check(Bitmap image,int tolerance,Action<Point> click) {
        if(!Active)return false;
        try {
            if(waiting) {
                if(now()<resumeAt)return false;
                // Rebase after the cooldown: a persistent change and click effects are not new events.
                detector.Start(image); waiting=false; return false;
            }
            return detector.Check(image,tolerance,delegate(Point p) {
                click(p); Completed++;
                if(Completed>=maximum)Dispose();
                else { waiting=true; resumeAt=now()+cooldown; }
            });
        } catch { Dispose(); throw; }
    }
    public void RestartCooldown() { if(Active && waiting)resumeAt=now()+cooldown; }
    public void Dispose() { Active=false; waiting=false; detector.Dispose(); }
}
static partial class Native {
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll",SetLastError=true)] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int dx,dy; public uint data,flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct Union { [FieldOffset(0)] public MouseInput mouse; [FieldOffset(0)] public KeyboardInput keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint type; public Union u; }
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    internal static void Click(Point p) { Click(p,0); }
    internal static void Click(Point p,bool right) { Click(p,right?1:0); }
    internal static void Click(Point p,int button) {
        Move(p);
        Input[] inputs = new Input[2];
        inputs[0].u.mouse.flags=button==1?8u:button==2?32u:2u; inputs[1].u.mouse.flags=button==1?16u:button==2?64u:4u;
        uint sent = SendInput(2, inputs, Marshal.SizeOf(typeof(Input)));
        if (sent != 2) {
            int error=Marshal.GetLastWin32Error();
            if (sent == 1) SendInput(1, new Input[] { inputs[1] }, Marshal.SizeOf(typeof(Input)));
            throw new InvalidOperationException("클릭 전송 차단 (오류 "+error+"). 대상과 같은 권한인지 확인하세요.");
        }
    }
}
sealed class Selector : Form {
    Point anchor; bool dragging; Rectangle box;
    public Rectangle Selected;
    public Selector() {
        FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual;
        Bounds=SystemInformation.VirtualScreen; TopMost=true; DoubleBuffered=true;
        BackColor=Color.Black; Opacity=0.28; Cursor=Cursors.Cross; KeyPreview=true;
        KeyDown += delegate(object s,KeyEventArgs e) { if(e.KeyCode==Keys.Escape) { DialogResult=DialogResult.Cancel; Close(); } };
        MouseDown += delegate(object s,MouseEventArgs e) { if(e.Button!=MouseButtons.Left)return; anchor=e.Location; dragging=true; Capture=true; };
        MouseMove += delegate(object s,MouseEventArgs e) { if(!dragging)return; box=Rectangle.FromLTRB(Math.Min(anchor.X,e.X),Math.Min(anchor.Y,e.Y),Math.Max(anchor.X,e.X),Math.Max(anchor.Y,e.Y)); Invalidate(); };
        MouseUp += delegate(object s,MouseEventArgs e) {
            if(!dragging || e.Button!=MouseButtons.Left)return; dragging=false; Capture=false;
            box=Rectangle.FromLTRB(Math.Min(anchor.X,e.X),Math.Min(anchor.Y,e.Y),Math.Max(anchor.X,e.X),Math.Max(anchor.Y,e.Y));
            if(box.Width<2 || box.Height<2) return;
            Selected=new Rectangle(PointToScreen(box.Location),box.Size); DialogResult=DialogResult.OK; Close();
        };
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); using(Pen p=new Pen(Color.Lime,3)) e.Graphics.DrawRectangle(p,box);
        e.Graphics.DrawString("드래그하여 영역 선택 · Esc 취소",SystemFonts.MessageBoxFont,Brushes.White,20,20);
    }
}
public sealed class Panel : Form {
    readonly RepeatDetection detector=new RepeatDetection();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    readonly MacroEditor editor=new MacroEditor();
    CheckBox linked, restrictMouse;
    Button testMacro, chooseMouseArea;
    Label mouseAreaLabel;
    Rectangle mouseArea, armedMouseArea;
    bool armedMouseOnly;
    MacroStep[] armedSteps;
    int armedRepeats;
    bool macroRunning;
    CancellationTokenSource execution;
    Rectangle region; bool selecting, capturingKey, pending;
    Keys hotkey=Keys.F8; int hotkeyId=1;
    Label status, area, keyLabel;
    Button select, toggle, bind;
    NumericUpDown tolerance, interval, repetitions, cooldown;
    public Panel() {
        Text="영역 변화 클릭 · 매크로 편집/녹화"; ClientSize=new Size(1210,630);
        AutoScroll=true;
        FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
        StartPosition=FormStartPosition.CenterScreen; Font=new Font("맑은 고딕",10);
        BackColor=Color.FromArgb(246,248,252); KeyPreview=true;
        Label title=new Label { Text="영역 변화 감지",Font=new Font(Font.FontFamily,19,FontStyle.Bold),Bounds=new Rectangle(24,20,450,40) }; Controls.Add(title);
        Controls.Add(new Label { Text="변화 위치 클릭 → 쿨타임 → 설정 횟수까지 감지",Bounds=new Rectangle(24,66,455,26) });
        area=new Label { Text="선택된 영역 없음",Bounds=new Rectangle(24,108,450,26) }; Controls.Add(area);
        select=ButtonAt("영역 선택",24,141,140); select.Click+=delegate { Choose(); };
        bind=ButtonAt("단축키 변경",180,141,140); bind.Click+=delegate { Stop("새 단축키를 누르세요."); capturingKey=true; keyLabel.Text="키 입력 대기 (Esc 취소)"; };
        keyLabel=new Label { Bounds=new Rectangle(24,189,450,26) }; Controls.Add(keyLabel);
        Controls.Add(new Label { Text="색상 차이 허용값 (0 = 모든 변화)",Bounds=new Rectangle(24,228,325,26) });
        tolerance=new NumericUpDown { Minimum=0,Maximum=254,Value=0,Bounds=new Rectangle(365,225,105,28) }; Controls.Add(tolerance);
        Controls.Add(new Label { Text="감지 간격 (초 · 0.05~60)",Bounds=new Rectangle(24,268,325,26) });
        interval=new NumericUpDown { Minimum=0.05m,Maximum=60m,DecimalPlaces=2,Increment=0.05m,Value=0.30m,Bounds=new Rectangle(365,265,105,28) }; Controls.Add(interval);
        Controls.Add(new Label { Text="최대 클릭 횟수 (총 1~10000회)",Bounds=new Rectangle(24,308,325,26) });
        repetitions=new NumericUpDown { Minimum=1,Maximum=10000,Value=1,Bounds=new Rectangle(365,305,105,28) }; Controls.Add(repetitions);
        Controls.Add(new Label { Text="클릭 후 쿨타임 (초 · 0~600)",Bounds=new Rectangle(24,348,325,26) });
        cooldown=new NumericUpDown { Minimum=0,Maximum=600,DecimalPlaces=2,Increment=0.1m,Value=1m,Bounds=new Rectangle(365,345,105,28) }; Controls.Add(cooldown);
        Controls.Add(new Label { Text="쿨타임이 끝난 화면을 새 기준으로 다음 변화 감지",Bounds=new Rectangle(24,385,450,26) });
        restrictMouse=new CheckBox { Text="마우스 작동 영역 제한 (키보드/문자 입력 제외)",Checked=true,Bounds=new Rectangle(24,425,450,28) }; Controls.Add(restrictMouse);
        chooseMouseArea=ButtonAt("작동 영역 지정",24,465,140); chooseMouseArea.Click+=delegate { ChooseMouseArea(); };
        mouseAreaLabel=new Label { Text="작동 영역 미지정\n감지 영역도 작동 영역 안에 지정하세요.",Bounds=new Rectangle(180,465,290,60) }; Controls.Add(mouseAreaLabel);
        toggle=ButtonAt("감지 시작",24,545,140); toggle.Click+=delegate { Toggle(); };
        status=new Label { Text="대기 중",Bounds=new Rectangle(180,545,290,65) }; Controls.Add(status);
        linked=new CheckBox { Text="변화 위치 클릭 후 아래 매크로 실행",Bounds=new Rectangle(512,22,670,30) }; Controls.Add(linked);
        editor.Location=new Point(500,65); Controls.Add(editor);
        editor.RecordingArea=delegate {
            if(!restrictMouse.Checked)return (Rectangle?)null;
            if(!RegionValid(mouseArea))throw new ArgumentException("녹화 전에 마우스 작동 영역을 지정하세요.");
            return (Rectangle?)mouseArea;
        };
        testMacro=ButtonAt("매크로만 테스트 (2초 후)",512,495,265); testMacro.Click+=delegate { TestMacro(); };
        Controls.Add(new Label { Text="중단: 왼쪽 정지 버튼 / ON·OFF 단축키\n녹화 종료: F9 · 녹화는 최대 10분/2000개 동작",Bounds=new Rectangle(800,495,380,60) });
        ClientSize=new Size(Math.Min(1210,Screen.PrimaryScreen.WorkingArea.Width-80),Math.Min(630,Screen.PrimaryScreen.WorkingArea.Height-100));
        timer.Interval=300; timer.Tick+=delegate { Tick(); };
        Shown+=delegate { if(!Native.RegisterHotKey(Handle,hotkeyId,0x4000, (uint)Keys.F8)) { hotkey=Keys.None; status.Text="F8 사용 중: 단축키를 변경하세요."; } UpdateKey(); };
        KeyDown+=OnKey;
        FormClosing+=delegate { if(execution!=null)execution.Cancel(); ReleaseMacroInput(); timer.Stop(); detector.Dispose(); Native.UnregisterHotKey(Handle,hotkeyId); timer.Dispose(); };
        Resize+=delegate { if(detector.Active || pending || macroRunning) Stop("창 상태 변경으로 정지했습니다."); };
        LocationChanged+=delegate { if(detector.Active || pending || macroRunning) Stop("창 이동으로 정지했습니다."); };
    }
    Button ButtonAt(string text,int x,int y,int w) { Button b=new Button { Text=text,Bounds=new Rectangle(x,y,w,36) }; Controls.Add(b); return b; }
    void UpdateKey() { keyLabel.Text="ON / OFF 단축키: "+(hotkey==Keys.None?"미설정":new KeysConverter().ConvertToString(hotkey)); }
    void OnKey(object sender,KeyEventArgs e) {
        if(!capturingKey)return; e.SuppressKeyPress=true;
        if(e.KeyCode==Keys.Escape) { capturingKey=false; UpdateKey(); return; }
        if(e.KeyCode==Keys.ControlKey || e.KeyCode==Keys.ShiftKey || e.KeyCode==Keys.Menu)return;
        uint mods=0x4000; if(e.Control)mods|=2; if(e.Alt)mods|=1; if(e.Shift)mods|=4;
        Keys candidate=e.KeyData;
        if(candidate==hotkey) { capturingKey=false; UpdateKey(); return; }
        int newId=hotkeyId==1?2:1;
        if(!Native.RegisterHotKey(Handle,newId,mods,(uint)e.KeyCode)) { status.Text="이미 사용 중인 단축키입니다. 다시 입력하세요."; return; }
        Native.UnregisterHotKey(Handle,hotkeyId); hotkeyId=newId; hotkey=candidate; capturingKey=false; UpdateKey(); status.Text="단축키 적용 완료";
    }
    protected override void WndProc(ref Message m) { if(m.Msg==0x312 && m.WParam.ToInt32()==hotkeyId && !capturingKey && !selecting && !editor.Picking) Toggle(); base.WndProc(ref m); }
    void Choose() {
        Stop("영역 선택 중"); selecting=true; Hide();
        try { using(Selector s=new Selector()) if(s.ShowDialog()==DialogResult.OK) { region=s.Selected; area.Text=string.Format("X {0}  Y {1}  /  {2} × {3} px",region.X,region.Y,region.Width,region.Height); } }
        finally { selecting=false; Show(); Activate(); status.Text="대기 중"; }
    }
    void ChooseMouseArea() {
        Stop("마우스 작동 영역 선택 중"); selecting=true; Hide();
        try { using(Selector selector=new Selector()) if(selector.ShowDialog()==DialogResult.OK) {
            mouseArea=selector.Selected;
            mouseAreaLabel.Text=string.Format("X {0}  Y {1}\n{2} × {3} px",mouseArea.X,mouseArea.Y,mouseArea.Width,mouseArea.Height);
        } } finally { selecting=false; Show(); Activate(); status.Text="대기 중"; }
    }
    Bitmap CaptureRegion() {
        Bitmap b=new Bitmap(region.Width,region.Height,PixelFormat.Format32bppArgb);
        try { using(Graphics g=Graphics.FromImage(b)) g.CopyFromScreen(region.Location,Point.Empty,region.Size,CopyPixelOperation.SourceCopy); return b; }
        catch { b.Dispose(); throw; }
    }
    bool RegionValid() { return RegionValid(region); }
    bool RegionValid(Rectangle target) {
        if(target.Width<2 || target.Height<2)return false;
        using(Region remaining=new Region(target)) {
            foreach(Screen screen in Screen.AllScreens)remaining.Exclude(screen.Bounds);
            using(Graphics g=CreateGraphics())return remaining.IsEmpty(g);
        }
    }
    void Toggle() {
        if(pending || detector.Active || macroRunning) { Stop("사용자가 정지했습니다."); return; }
        if(selecting || capturingKey || editor.Picking)return;
        if(!RegionValid()) { status.Text="유효한 화면 영역을 선택하세요."; return; }
        if(WindowState!=FormWindowState.Minimized && Bounds.IntersectsWith(region)) { status.Text="컨트롤 패널을 감지 영역 밖으로 옮기세요."; return; }
        try {
            ArmMouseScope();
            if(armedMouseOnly && !armedMouseArea.Contains(region))throw new ArgumentException("감지 영역 전체를 마우스 작동 영역 안에 지정하세요.");
            armedSteps=linked.Checked?ValidateMacro():new MacroStep[0]; armedRepeats=editor.Repeats;
        }
        catch(ArgumentException ex) { status.Text=ex.Message; return; }
        timer.Interval=decimal.ToInt32(interval.Value*1000m);
        pending=true; Editing(false); toggle.Text="감지 정지";
        status.Text="기준 화면 준비 중…"; timer.Start();
    }
    void Editing(bool enabled) { select.Enabled=bind.Enabled=tolerance.Enabled=interval.Enabled=repetitions.Enabled=cooldown.Enabled=editor.Enabled=linked.Enabled=testMacro.Enabled=restrictMouse.Enabled=chooseMouseArea.Enabled=enabled; }
    void Stop(string text) {
        timer.Stop(); pending=false; detector.Dispose();
        if(execution!=null)execution.Cancel();
        if(macroRunning) { status.Text=text+" · 중단 중"; toggle.Enabled=false; return; }
        Editing(true); toggle.Enabled=true; toggle.Text="감지 시작"; status.Text=text;
    }
    MacroStep[] ValidateMacro() {
        MacroStep[] steps=editor.Snapshot();
        if(armedMouseOnly)steps=MouseScope.Prepare(steps,armedMouseArea);
        foreach(MacroStep step in steps) if((step.Kind==StepKind.Key || step.Kind==StepKind.KeyDown || step.Kind==StepKind.KeyUp) && (step.Key&Keys.KeyCode)==(hotkey&Keys.KeyCode))
            throw new ArgumentException("매크로 키와 감지 ON/OFF 단축키를 다르게 설정하세요.");
        return steps;
    }
    void TestMacro() {
        if(macroRunning || pending || detector.Active || capturingKey || editor.Picking)return;
        try {
            ArmMouseScope(); armedSteps=ValidateMacro(); armedRepeats=editor.Repeats;
            foreach(MacroStep step in armedSteps) if(step.Kind==StepKind.DetectedClick)throw new ArgumentException("변화 위치 클릭 동작은 실제 감지 후에만 사용할 수 있습니다.");
            RunMacro(Point.Empty,true);
        } catch(ArgumentException ex) { status.Text=ex.Message; }
    }
    void ArmMouseScope() {
        armedMouseOnly=restrictMouse.Checked; armedMouseArea=mouseArea;
        if(armedMouseOnly && !RegionValid(armedMouseArea))throw new ArgumentException("유효한 화면 안에서 마우스 작동 영역을 지정하세요.");
    }
    void ExecuteMacroInput(MacroStep step,Point detected) {
        if(armedMouseOnly) {
            if(!RegionValid(armedMouseArea))throw new InvalidOperationException("작동 영역의 화면 구성이 변경되어 정지했습니다.");
            MouseScope.RequireStep(armedMouseArea,step,detected);
        }
        Native.Execute(step,detected);
    }
    void ReleaseMacroInput() { Native.ReleaseHeld(armedMouseOnly?(Rectangle?)armedMouseArea:null); }
    async void RunMacro(Point target,bool manual) {
        timer.Stop(); macroRunning=true; Editing(false); toggle.Text="매크로 정지";
        CancellationTokenSource current=new CancellationTokenSource(); execution=current;
        string failure=null;
        try {
            if(manual) { status.Text="2초 후 테스트 · 입력할 창을 선택하세요."; await Task.Delay(2000,current.Token); }
            for(int pass=0;pass<armedRepeats;pass++) {
                int iteration=pass;
                try { await MacroRunner.RunAsync(armedSteps,target,ExecuteMacroInput,current.Token,
                    delegate(int i) { status.Text=string.Format("매크로 {0}/{1}회 · {2}/{3}단계",iteration+1,armedRepeats,i+1,armedSteps.Length); },
                    delegate(int ms,CancellationToken token) { return Task.Delay(ms,token); }); }
                finally { ReleaseMacroInput(); }
            }
        } catch(OperationCanceledException) { failure="매크로 정지 · 남은 동작 취소"; }
        catch(Exception ex) { failure="매크로 오류: "+ex.Message; }
        finally {
            ReleaseMacroInput(); execution=null; current.Dispose(); macroRunning=false;
            if(!IsDisposed && !Disposing) {
                toggle.Enabled=true;
                if(failure!=null)Stop(failure);
                else if(manual)Stop("매크로 테스트 완료");
                else if(!detector.Active)Stop(string.Format("{0}회 감지/클릭 및 매크로 완료",detector.Completed));
                else { detector.RestartCooldown(); toggle.Text="감지 정지"; DetectionStatus(); timer.Start(); }
            }
        }
    }
    void DetectionStatus() {
        string progress=string.Format("{0}/{1}회",detector.Completed,(int)repetitions.Value);
        status.Text=detector.Waiting ? string.Format("{0} · 쿨타임 {1:0.00}초",progress,detector.RemainingMilliseconds/1000m)
            : string.Format("{0} · 감지 중 ({1:0.##}초)",progress,timer.Interval/1000m);
    }
    void Tick() {
        try {
            if(!RegionValid()) { Stop("화면 구성이 변경되어 정지했습니다."); return; }
            if(armedMouseOnly && !RegionValid(armedMouseArea)) { Stop("마우스 작동 영역의 화면 구성이 변경되어 정지했습니다."); return; }
            if(WindowState!=FormWindowState.Minimized && Bounds.IntersectsWith(region)) { Stop("패널이 감지 영역에 겹쳐 정지했습니다."); return; }
            using(Bitmap image=CaptureRegion()) {
                if(pending) { detector.Start(image,(int)repetitions.Value,decimal.ToInt32(cooldown.Value*1000m)); pending=false; DetectionStatus(); return; }
                Point target=Point.Empty;
                bool clicked=detector.Check(image,(int)tolerance.Value,delegate(Point local) {
                    target=new Point(region.X+local.X,region.Y+local.Y);
                    if(armedMouseOnly)MouseScope.RequirePoint(armedMouseArea,target);
                    Native.Click(target);
                });
                if(clicked && armedSteps.Length>0)RunMacro(target,false);
                else if(!detector.Active)Stop(string.Format("{0}회 클릭 완료 · 자동 정지",detector.Completed));
                else DetectionStatus();
            }
        } catch(Exception ex) { Stop("오류로 정지: "+ex.Message); }
    }
}
static class Program {
    [STAThread] static void Main() {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new Panel());
    }
}
}
