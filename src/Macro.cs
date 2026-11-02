using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace RegionChangeClicker {
public enum StepKind { DetectedClick, PositionClick, Text, Key, Delay, MouseMove, MouseWheel, KeyDown, KeyUp, MouseDown, MouseUp }
public sealed class MacroStep {
    public StepKind Kind;
    public int X, Y, Milliseconds = 500;
    public bool RightButton;
    public string Text = "";
    public Keys Key = Keys.Enter;
    public int Button, Wheel=120;
    public MacroStep Copy() { return (MacroStep)MemberwiseClone(); }
    public void Validate() {
        if (!Enum.IsDefined(typeof(StepKind), Kind)) throw new ArgumentException("알 수 없는 동작입니다.");
        if (Kind == StepKind.Delay && (Milliseconds < 0 || Milliseconds > 600000)) throw new ArgumentException("지연은 0~600000ms 범위입니다.");
        if (Kind == StepKind.Text && (string.IsNullOrEmpty(Text) || Text.Length > 2000)) throw new ArgumentException("문자는 1~2000자로 입력하세요.");
        if (Kind == StepKind.Key && !ValidKey(Key)) throw new ArgumentException("일반 키와 Ctrl/Alt/Shift 조합을 사용하세요.");
        if ((Kind==StepKind.KeyDown || Kind==StepKind.KeyUp) && ((int)Key<1 || (int)Key>254)) throw new ArgumentException("키 코드는 1~254 범위입니다.");
        if ((Kind==StepKind.MouseDown || Kind==StepKind.MouseUp || Kind==StepKind.PositionClick || Kind==StepKind.DetectedClick) && (Button<0 || Button>2)) throw new ArgumentException("마우스 버튼 값이 잘못되었습니다.");
        if (Kind==StepKind.MouseWheel && (Wheel==0 || Wheel < -12000 || Wheel>12000)) throw new ArgumentException("휠 값은 -12000~12000의 0이 아닌 값입니다.");
        if(X < -100000 || X > 100000 || Y < -100000 || Y > 100000) throw new ArgumentException("좌표 범위를 초과했습니다.");
    }
    public static bool ValidKey(Keys key) {
        Keys code = key & Keys.KeyCode;
        return ((int)key & ~((int)Keys.KeyCode | (int)Keys.Modifiers)) == 0 &&
            code >= Keys.Back && code <= Keys.OemClear &&
            code != Keys.ShiftKey && code != Keys.ControlKey && code != Keys.Menu &&
            code != Keys.LWin && code != Keys.RWin && code != Keys.LButton && code != Keys.RButton &&
            code != Keys.LShiftKey && code != Keys.RShiftKey && code != Keys.LControlKey && code != Keys.RControlKey &&
            code != Keys.LMenu && code != Keys.RMenu;
    }
    public override string ToString() {
        switch (Kind) {
            case StepKind.DetectedClick: return "변화 위치 " + (RightButton ? "오른쪽" : "왼쪽") + " 클릭";
            case StepKind.PositionClick: return string.Format("좌표 ({0}, {1}) {2} 클릭", X, Y, RightButton ? "오른쪽" : "왼쪽");
            case StepKind.Text: return "문자 입력: " + (Text ?? "").Replace("\r", "").Replace("\n", " ↵ ");
            case StepKind.Key: return "키 입력: " + new KeysConverter().ConvertToString(Key);
            case StepKind.MouseMove: return string.Format("마우스 이동 ({0}, {1})",X,Y);
            case StepKind.MouseWheel: return string.Format("마우스 휠 {0} ({1}, {2})",Wheel,X,Y);
            case StepKind.KeyDown: return "키 누르기: "+Key;
            case StepKind.KeyUp: return "키 떼기: "+Key;
            case StepKind.MouseDown: return string.Format("{0} 버튼 누르기 ({1}, {2})",ButtonName(Button),X,Y);
            case StepKind.MouseUp: return string.Format("{0} 버튼 떼기 ({1}, {2})",ButtonName(Button),X,Y);
            default: return "대기 " + Milliseconds + "ms";
        }
    }
    public static string ButtonName(int button) { return button==1?"오른쪽":button==2?"가운데":"왼쪽"; }
}
public sealed class MacroFile {
    public int Version=1;
    public int Repeats=1;
    public MacroStep[] Steps=new MacroStep[0];
}
public static class MacroStorage {
    public static void Save(string path,MacroStep[] steps,int repeats) {
        steps=MacroRunner.Snapshot(steps);
        if(repeats<1 || repeats>10000)throw new ArgumentException("반복 횟수는 1~10000회입니다.");
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using(FileStream stream=new FileStream(temp,FileMode.CreateNew))new XmlSerializer(typeof(MacroFile)).Serialize(stream,new MacroFile { Steps=steps,Repeats=repeats });
            if(File.Exists(path))File.Replace(temp,path,null); else File.Move(temp,path);
        } finally { if(File.Exists(temp))File.Delete(temp); }
    }
    public static MacroFile Load(string path) {
        if(new FileInfo(path).Length>4*1024*1024)throw new ArgumentException("매크로 파일은 4MB 이하만 지원합니다.");
        XmlReaderSettings settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024 };
        MacroFile result;
        using(XmlReader reader=XmlReader.Create(path,settings)) result=(MacroFile)new XmlSerializer(typeof(MacroFile)).Deserialize(reader);
        if(result==null || result.Version!=1 || result.Repeats<1 || result.Repeats>10000 || result.Steps==null)throw new ArgumentException("지원하지 않는 매크로 파일입니다.");
        result.Steps=MacroRunner.Snapshot(result.Steps); return result;
    }
}
public static class MacroRunner {
    public static MacroStep[] Snapshot(IEnumerable<MacroStep> steps) {
        List<MacroStep> result = new List<MacroStep>();
        foreach (MacroStep s in steps) { if(s==null)throw new ArgumentException("빈 동작입니다."); s.Validate(); result.Add(s.Copy()); }
        if (result.Count == 0 || result.Count > 2000) throw new ArgumentException("동작을 1~2000개 설정하세요.");
        return result.ToArray();
    }
    public static async Task RunAsync(MacroStep[] steps, Point detected, Action<MacroStep,Point> input,
        CancellationToken token, Action<int> progress, Func<int,CancellationToken,Task> delay) {
        foreach (MacroStep s in steps) s.Validate();
        for (int i = 0; i < steps.Length; i++) {
            await Task.Yield(); // Let the UI deliver stop/hotkey/close messages between actions.
            token.ThrowIfCancellationRequested();
            if (progress != null) progress(i);
            token.ThrowIfCancellationRequested();
            if (steps[i].Kind == StepKind.Delay) await delay(steps[i].Milliseconds, token);
            else input(steps[i], detected);
        }
        token.ThrowIfCancellationRequested();
    }
}
public static class MouseScope {
    public static bool IsMouse(StepKind kind) {
        return kind==StepKind.DetectedClick || kind==StepKind.PositionClick || kind==StepKind.MouseMove || kind==StepKind.MouseWheel || kind==StepKind.MouseDown || kind==StepKind.MouseUp;
    }
    public static void RequirePoint(Rectangle area,Point point) {
        if(area.Width<2 || area.Height<2 || !area.Contains(point))throw new ArgumentException("마우스 동작 좌표가 지정한 작동 영역 밖입니다.");
    }
    public static void RequireStep(Rectangle area,MacroStep step,Point detected) {
        if(step.Kind==StepKind.Delay)return;
        if(!IsMouse(step.Kind))throw new ArgumentException("마우스 영역 제한 모드에서는 키보드 입력을 실행하지 않습니다.");
        RequirePoint(area,step.Kind==StepKind.DetectedClick?detected:new Point(step.X,step.Y));
    }
    public static MacroStep[] Prepare(IEnumerable<MacroStep> source,Rectangle area) {
        if(area.Width<2 || area.Height<2)throw new ArgumentException("마우스 작동 영역을 먼저 지정하세요.");
        List<MacroStep> result=new List<MacroStep>(); bool hasMouse=false;
        foreach(MacroStep step in MacroRunner.Snapshot(source)) {
            if(step.Kind!=StepKind.Delay && !IsMouse(step.Kind))continue;
            if(IsMouse(step.Kind)) {
                hasMouse=true;
                if(step.Kind!=StepKind.DetectedClick)RequirePoint(area,new Point(step.X,step.Y));
            }
            result.Add(step);
        }
        if(!hasMouse)throw new ArgumentException("제한 영역 안에서 실행할 마우스 동작을 1개 이상 추가하세요.");
        return result.ToArray();
    }
}
static partial class Native {
    static readonly HashSet<Keys> heldKeys=new HashSet<Keys>();
    static readonly HashSet<int> heldButtons=new HashSet<int>();
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort key, scan; public uint flags,time; public UIntPtr extra; }
    static Input KeyInput(ushort key, ushort scan, uint flags) {
        Input i = new Input(); i.type = 1; i.u.keyboard.key = key; i.u.keyboard.scan = scan; i.u.keyboard.flags = flags; return i;
    }
    static bool Extended(Keys key) { return key == Keys.RControlKey || key == Keys.RMenu || key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.Insert || key == Keys.Delete || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Divide || key == Keys.NumLock; }
    internal static void Press(Keys chord) {
        if (!MacroStep.ValidKey(chord)) throw new ArgumentException("지원하지 않는 키입니다.");
        List<Keys> keys = new List<Keys>();
        if ((chord & Keys.Control) != 0) keys.Add(Keys.ControlKey);
        if ((chord & Keys.Alt) != 0) keys.Add(Keys.Menu);
        if ((chord & Keys.Shift) != 0) keys.Add(Keys.ShiftKey);
        keys.Add(chord & Keys.KeyCode);
        List<Input> inputs = new List<Input>(), releases = new List<Input>();
        foreach (Keys key in keys) inputs.Add(KeyInput((ushort)key,0,Extended(key) ? 1u : 0u));
        for (int k = keys.Count - 1; k >= 0; k--) releases.Add(KeyInput((ushort)keys[k],0,2u | (Extended(keys[k]) ? 1u : 0u)));
        inputs.AddRange(releases);
        if (SendInput((uint)inputs.Count,inputs.ToArray(),Marshal.SizeOf(typeof(Input))) != inputs.Count) {
            SendInput((uint)releases.Count,releases.ToArray(),Marshal.SizeOf(typeof(Input)));
            throw new InvalidOperationException("키 입력 실패. 대상 프로그램의 권한을 확인하세요.");
        }
    }
    internal static void TypeText(string text) {
        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (c == '\r') { if (i+1 < text.Length && text[i+1]=='\n') i++; Press(Keys.Enter); continue; }
            if (c == '\n') { Press(Keys.Enter); continue; }
            if (c == '\t') { Press(Keys.Tab); continue; }
            Input down = KeyInput(0,c,4), up = KeyInput(0,c,6);
            uint sent = SendInput(2,new Input[] { down,up },Marshal.SizeOf(typeof(Input)));
            if (sent != 2) {
                if (sent == 1) SendInput(1,new Input[] { up },Marshal.SizeOf(typeof(Input)));
                throw new InvalidOperationException("문자 입력 실패. 대상 프로그램의 권한을 확인하세요.");
            }
        }
    }
    internal static void Execute(MacroStep step, Point detected) {
        switch (step.Kind) {
            case StepKind.DetectedClick: Click(detected, step.RightButton?1:step.Button); break;
            case StepKind.PositionClick:
                Point p = new Point(step.X,step.Y);
                bool valid = false; foreach (Screen screen in Screen.AllScreens) if (screen.Bounds.Contains(p)) valid = true;
                if (!valid) throw new InvalidOperationException("클릭 좌표가 현재 화면 밖입니다.");
                Click(p,step.RightButton?1:step.Button); break;
            case StepKind.Text: TypeText(step.Text); break;
            case StepKind.Key: Press(step.Key); break;
            case StepKind.MouseMove: Move(new Point(step.X,step.Y)); break;
            case StepKind.MouseWheel:
                Move(new Point(step.X,step.Y)); Input wheel=new Input(); wheel.u.mouse.flags=0x800; wheel.u.mouse.data=unchecked((uint)step.Wheel); SendOne(wheel); break;
            case StepKind.KeyDown: RawKey(step.Key,false); break;
            case StepKind.KeyUp: RawKey(step.Key,true); break;
            case StepKind.MouseDown: RawMouse(step.Button,false,new Point(step.X,step.Y)); break;
            case StepKind.MouseUp: RawMouse(step.Button,true,new Point(step.X,step.Y)); break;
        }
    }
    static void Move(Point p) {
        bool visible=false; foreach(Screen screen in Screen.AllScreens)if(screen.Bounds.Contains(p))visible=true;
        if(!visible || !SetCursorPos(p.X,p.Y))throw new InvalidOperationException("마우스 좌표가 현재 화면 밖입니다.");
    }
    static void SendOne(Input input) {
        if(SendInput(1,new Input[] { input },Marshal.SizeOf(typeof(Input)))!=1)throw new InvalidOperationException("입력 전송 실패. 대상 권한을 확인하세요.");
    }
    static void RawKey(Keys key,bool up) {
        SendOne(KeyInput((ushort)key,0,(Extended(key)?1u:0u)|(up?2u:0u)));
        if(up)heldKeys.Remove(key); else heldKeys.Add(key);
    }
    static void RawMouse(int button,bool up,Point p) {
        Move(p); Input input=new Input(); input.u.mouse.flags=button==1?(up?16u:8u):button==2?(up?64u:32u):(up?4u:2u);
        SendOne(input); if(up)heldButtons.Remove(button); else heldButtons.Add(button);
    }
    internal static void ReleaseHeld() { ReleaseHeld(null); }
    internal static void ReleaseHeld(Rectangle? area) {
        foreach(Keys key in new List<Keys>(heldKeys)) {
            Input input=KeyInput((ushort)key,0,2u|(Extended(key)?1u:0u)); SendInput(1,new Input[] {input},Marshal.SizeOf(typeof(Input)));
        }
        if(heldButtons.Count>0 && area.HasValue) {
            Rectangle allowed=area.Value; Point current=Cursor.Position;
            if(!allowed.Contains(current))SetCursorPos(Math.Max(allowed.Left,Math.Min(allowed.Right-1,current.X)),Math.Max(allowed.Top,Math.Min(allowed.Bottom-1,current.Y)));
        }
        foreach(int button in new List<int>(heldButtons)) {
            Input input=new Input(); input.u.mouse.flags=button==1?16u:button==2?64u:4u; SendInput(1,new Input[] {input},Marshal.SizeOf(typeof(Input)));
        }
        heldKeys.Clear(); heldButtons.Clear();
    }
}
}
