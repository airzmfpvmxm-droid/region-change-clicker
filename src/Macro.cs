using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RegionChangeClicker {
public enum StepKind { DetectedClick, PositionClick, Text, Key, Delay }
public sealed class MacroStep {
    public StepKind Kind;
    public int X, Y, Milliseconds = 500;
    public bool RightButton;
    public string Text = "";
    public Keys Key = Keys.Enter;
    public MacroStep Copy() { return (MacroStep)MemberwiseClone(); }
    public void Validate() {
        if (!Enum.IsDefined(typeof(StepKind), Kind)) throw new ArgumentException("알 수 없는 동작입니다.");
        if (Kind == StepKind.Delay && (Milliseconds < 0 || Milliseconds > 600000)) throw new ArgumentException("지연은 0~600000ms 범위입니다.");
        if (Kind == StepKind.Text && (string.IsNullOrEmpty(Text) || Text.Length > 2000)) throw new ArgumentException("문자는 1~2000자로 입력하세요.");
        if (Kind == StepKind.Key && !ValidKey(Key)) throw new ArgumentException("일반 키와 Ctrl/Alt/Shift 조합을 사용하세요.");
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
            default: return "대기 " + Milliseconds + "ms";
        }
    }
}
public static class MacroRunner {
    public static MacroStep[] Snapshot(IEnumerable<MacroStep> steps) {
        List<MacroStep> result = new List<MacroStep>();
        foreach (MacroStep s in steps) { s.Validate(); result.Add(s.Copy()); }
        if (result.Count == 0 || result.Count > 100) throw new ArgumentException("동작을 1~100개 설정하세요.");
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
static partial class Native {
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort key, scan; public uint flags,time; public UIntPtr extra; }
    static Input KeyInput(ushort key, ushort scan, uint flags) {
        Input i = new Input(); i.type = 1; i.u.keyboard.key = key; i.u.keyboard.scan = scan; i.u.keyboard.flags = flags; return i;
    }
    static bool Extended(Keys key) { return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.Insert || key == Keys.Delete || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Divide || key == Keys.NumLock; }
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
            case StepKind.DetectedClick: Click(detected, step.RightButton); break;
            case StepKind.PositionClick:
                Point p = new Point(step.X,step.Y);
                bool valid = false; foreach (Screen screen in Screen.AllScreens) if (screen.Bounds.Contains(p)) valid = true;
                if (!valid) throw new InvalidOperationException("클릭 좌표가 현재 화면 밖입니다.");
                Click(p,step.RightButton); break;
            case StepKind.Text: TypeText(step.Text); break;
            case StepKind.Key: Press(step.Key); break;
        }
    }
}
}
