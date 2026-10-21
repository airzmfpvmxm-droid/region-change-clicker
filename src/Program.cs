using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
static class Native {
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int dx,dy; public uint data,flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct Union { [FieldOffset(0)] public MouseInput mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint type; public Union u; }
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    internal static void Click(Point p) {
        if (!SetCursorPos(p.X,p.Y)) throw new InvalidOperationException("마우스를 이동할 수 없습니다.");
        Input[] inputs = new Input[2];
        inputs[0].u.mouse.flags = 2; inputs[1].u.mouse.flags = 4;
        uint sent = SendInput(2, inputs, Marshal.SizeOf(typeof(Input)));
        if (sent != 2) {
            if (sent == 1) SendInput(1, new Input[] { inputs[1] }, Marshal.SizeOf(typeof(Input)));
            throw new InvalidOperationException("클릭 전송 실패. 대상 프로그램의 권한을 확인하세요.");
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
    readonly OneShot detector=new OneShot();
    readonly Timer timer=new Timer();
    Rectangle region; bool selecting, capturingKey, pending;
    Keys hotkey=Keys.F8; int hotkeyId=1;
    Label status, area, keyLabel;
    Button select, toggle, bind;
    NumericUpDown tolerance;
    public Panel() {
        Text="영역 변화 클릭 매크로"; ClientSize=new Size(500,355);
        FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
        StartPosition=FormStartPosition.CenterScreen; Font=new Font("맑은 고딕",10);
        BackColor=Color.FromArgb(246,248,252); KeyPreview=true;
        Label title=new Label { Text="영역 변화 감지",Font=new Font(Font.FontFamily,19,FontStyle.Bold),Bounds=new Rectangle(24,20,450,40) }; Controls.Add(title);
        Controls.Add(new Label { Text="1초마다 확인 → 변화 위치 1회 클릭 → 자동 정지",Bounds=new Rectangle(24,66,455,26) });
        area=new Label { Text="선택된 영역 없음",Bounds=new Rectangle(24,108,450,26) }; Controls.Add(area);
        select=ButtonAt("영역 선택",24,141,140); select.Click+=delegate { Choose(); };
        bind=ButtonAt("단축키 변경",180,141,140); bind.Click+=delegate { Stop("새 단축키를 누르세요."); capturingKey=true; keyLabel.Text="키 입력 대기 (Esc 취소)"; };
        keyLabel=new Label { Bounds=new Rectangle(24,189,450,26) }; Controls.Add(keyLabel);
        Controls.Add(new Label { Text="색상 차이 허용값 (0 = 모든 변화)",Bounds=new Rectangle(24,228,325,26) });
        tolerance=new NumericUpDown { Minimum=0,Maximum=254,Value=0,Bounds=new Rectangle(365,225,105,28) }; Controls.Add(tolerance);
        toggle=ButtonAt("감지 시작",24,272,140); toggle.Click+=delegate { Toggle(); };
        status=new Label { Text="대기 중",Bounds=new Rectangle(180,275,290,65) }; Controls.Add(status);
        timer.Interval=1000; timer.Tick+=delegate { Tick(); };
        Shown+=delegate { if(!Native.RegisterHotKey(Handle,hotkeyId,0x4000, (uint)Keys.F8)) { hotkey=Keys.None; status.Text="F8 사용 중: 단축키를 변경하세요."; } UpdateKey(); };
        KeyDown+=OnKey;
        FormClosing+=delegate { timer.Stop(); detector.Dispose(); Native.UnregisterHotKey(Handle,hotkeyId); timer.Dispose(); };
        Resize+=delegate { if(detector.Active) Stop("창 상태 변경으로 정지했습니다."); };
        LocationChanged+=delegate { if(detector.Active) Stop("창 이동으로 정지했습니다."); };
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
    protected override void WndProc(ref Message m) { if(m.Msg==0x312 && m.WParam.ToInt32()==hotkeyId && !capturingKey && !selecting) Toggle(); base.WndProc(ref m); }
    void Choose() {
        Stop("영역 선택 중"); selecting=true; Hide();
        try { using(Selector s=new Selector()) if(s.ShowDialog()==DialogResult.OK) { region=s.Selected; area.Text=string.Format("X {0}  Y {1}  /  {2} × {3} px",region.X,region.Y,region.Width,region.Height); } }
        finally { selecting=false; Show(); Activate(); status.Text="대기 중"; }
    }
    Bitmap CaptureRegion() {
        Bitmap b=new Bitmap(region.Width,region.Height,PixelFormat.Format32bppArgb);
        try { using(Graphics g=Graphics.FromImage(b)) g.CopyFromScreen(region.Location,Point.Empty,region.Size,CopyPixelOperation.SourceCopy); return b; }
        catch { b.Dispose(); throw; }
    }
    bool RegionValid() {
        if(region.Width<2 || region.Height<2)return false;
        using(Region remaining=new Region(region)) {
            foreach(Screen screen in Screen.AllScreens)remaining.Exclude(screen.Bounds);
            using(Graphics g=CreateGraphics())return remaining.IsEmpty(g);
        }
    }
    void Toggle() {
        if(pending || detector.Active) { Stop("사용자가 정지했습니다."); return; }
        if(selecting || capturingKey)return;
        if(!RegionValid()) { status.Text="유효한 화면 영역을 선택하세요."; return; }
        if(WindowState!=FormWindowState.Minimized && Bounds.IntersectsWith(region)) { status.Text="컨트롤 패널을 감지 영역 밖으로 옮기세요."; return; }
        pending=true; select.Enabled=bind.Enabled=tolerance.Enabled=false; toggle.Text="감지 정지";
        status.Text="기준 화면 준비 중…"; timer.Start();
    }
    void Stop(string text) { timer.Stop(); pending=false; detector.Dispose(); select.Enabled=bind.Enabled=tolerance.Enabled=true; toggle.Text="감지 시작"; status.Text=text; }
    void Tick() {
        try {
            if(!RegionValid()) { Stop("화면 구성이 변경되어 정지했습니다."); return; }
            if(WindowState!=FormWindowState.Minimized && Bounds.IntersectsWith(region)) { Stop("패널이 감지 영역에 겹쳐 정지했습니다."); return; }
            using(Bitmap image=CaptureRegion()) {
                if(pending) { detector.Start(image); pending=false; status.Text="감지 중 · 1초 간격"; return; }
                detector.Check(image,(int)tolerance.Value,delegate(Point local) {
                    Point target=new Point(region.X+local.X,region.Y+local.Y);
                    Stop("변화 감지: 클릭 전송 중…"); Native.Click(target);
                    status.Text=string.Format("클릭 완료 ({0}, {1}) · 자동 정지",target.X,target.Y);
                });
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
