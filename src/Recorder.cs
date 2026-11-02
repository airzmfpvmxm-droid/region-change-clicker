using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RegionChangeClicker {
// This model can be tested without installing any system hooks.
public sealed class RecordingBuffer {
    readonly List<MacroStep> steps=new List<MacroStep>();
    long last;
    public int Count { get { return steps.Count; } }
    public void Add(MacroStep step,long milliseconds) {
        step.Validate();
        if(steps.Count>=1996)throw new InvalidOperationException("녹화 동작 수 제한에 도달했습니다.");
        long elapsed=Math.Max(0,milliseconds-last);
        if(elapsed>0)steps.Add(new MacroStep { Kind=StepKind.Delay,Milliseconds=(int)Math.Min(elapsed,600000) });
        steps.Add(step.Copy()); last=milliseconds;
    }
    public MacroStep[] Snapshot() { return MacroRunner.Snapshot(steps); }
}
sealed class RecorderDialog : Form {
    delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int type,Hook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string module);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] struct KeyboardData { public uint key,scan,flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MouseData { public Point point; public uint data,flags,time; public UIntPtr extra; }
    readonly RecordingBuffer buffer=new RecordingBuffer();
    readonly Label info=new Label();
    readonly Stopwatch clock=new Stopwatch();
    readonly System.Windows.Forms.Timer update=new System.Windows.Forms.Timer();
    readonly Hook keyCallback,mouseCallback;
    readonly Rectangle? recordingArea;
    readonly HashSet<int> recordedButtons=new HashSet<int>();
    IntPtr keyHook,mouseHook;
    long lastMove=-50;
    bool recording, ending;
    public MacroStep[] Result;
    public RecorderDialog(Rectangle? area=null) {
        recordingArea=area;
        Text="매크로 녹화"; ClientSize=new Size(430,165); Font=new Font("맑은 고딕",10);
        FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=MinimizeBox=false; TopMost=true; StartPosition=FormStartPosition.CenterScreen;
        info.Bounds=new Rectangle(18,15,395,75); info.Text="2초 후 녹화를 시작합니다.\n조작을 마치면 F9 또는 녹화 완료를 누르세요."; Controls.Add(info);
        Button finish=new Button { Text="녹화 완료 (F9)",Bounds=new Rectangle(18,105,185,36) }; finish.Click+=delegate { Finish(true); }; Controls.Add(finish);
        Button cancel=new Button { Text="취소",Bounds=new Rectangle(220,105,185,36) }; cancel.Click+=delegate { Finish(false); }; Controls.Add(cancel);
        keyCallback=OnKeyboard; mouseCallback=OnMouse;
        Shown+=async delegate {
            await Task.Delay(2000); if(IsDisposed || ending)return;
            try {
                IntPtr module=GetModuleHandle(null);
                keyHook=SetWindowsHookEx(13,keyCallback,module,0);
                mouseHook=SetWindowsHookEx(14,mouseCallback,module,0);
                if(keyHook==IntPtr.Zero || mouseHook==IntPtr.Zero)throw new InvalidOperationException("녹화 입력을 등록할 수 없습니다.");
                recording=true; clock.Start(); update.Start();
            } catch(Exception ex) { MessageBox.Show(this,ex.Message,"녹화 오류"); Finish(false); }
        };
        update.Interval=200; update.Tick+=delegate { info.Text=string.Format("녹화 중 · {0:0.0}초 · {1}개 동작\nF9로 종료 · 이 창 내부 조작은 기록하지 않습니다.",clock.Elapsed.TotalSeconds,buffer.Count); if(clock.ElapsedMilliseconds>=600000)Finish(true); };
        FormClosing+=delegate { ending=true; RemoveHooks(); }; FormClosed+=delegate { update.Dispose(); };
    }
    void RemoveHooks() {
        recording=false; update.Stop();
        if(keyHook!=IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook=IntPtr.Zero; }
        if(mouseHook!=IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook=IntPtr.Zero; }
    }
    void Finish(bool accept) {
        if(ending)return; ending=true; RemoveHooks();
        if(accept && buffer.Count>0) { Result=buffer.Snapshot(); DialogResult=DialogResult.OK; }
        else DialogResult=DialogResult.Cancel;
        Close();
    }
    void Add(MacroStep step) {
        try { buffer.Add(step,clock.ElapsedMilliseconds); }
        catch { if(!ending)BeginInvoke(new Action(delegate { Finish(true); })); }
    }
    IntPtr OnKeyboard(int code,IntPtr message,IntPtr data) {
        if(code>=0 && recording) {
            try {
                KeyboardData k=(KeyboardData)Marshal.PtrToStructure(data,typeof(KeyboardData));
                int msg=message.ToInt32();
                if(k.key==(uint)Keys.F9) {
                    if(msg==0x100 || msg==0x104)BeginInvoke(new Action(delegate { Finish(true); }));
                    return new IntPtr(1);
                }
                if(!recordingArea.HasValue && (k.flags&0x10)==0 && GetForegroundWindow()!=Handle) {
                    if(msg==0x100 || msg==0x104)Add(new MacroStep { Kind=StepKind.KeyDown,Key=(Keys)k.key });
                    else if(msg==0x101 || msg==0x105)Add(new MacroStep { Kind=StepKind.KeyUp,Key=(Keys)k.key });
                }
            } catch { if(!ending)BeginInvoke(new Action(delegate { Finish(false); })); }
        }
        return CallNextHookEx(keyHook,code,message,data);
    }
    IntPtr OnMouse(int code,IntPtr message,IntPtr data) {
        if(code>=0 && recording) {
            try {
                MouseData m=(MouseData)Marshal.PtrToStructure(data,typeof(MouseData));
                if(recordingArea.HasValue && !recordingArea.Value.Contains(m.point)) {
                    if((m.flags&1)==0 && recordedButtons.Count>0) {
                        Rectangle a=recordingArea.Value;
                        foreach(int button in new List<int>(recordedButtons))Add(new MacroStep {Kind=StepKind.MouseUp,Button=button,X=Math.Max(a.Left,Math.Min(a.Right-1,m.point.X)),Y=Math.Max(a.Top,Math.Min(a.Bottom-1,m.point.Y))});
                        recordedButtons.Clear();
                    }
                    return CallNextHookEx(mouseHook,code,message,data);
                }
                if((m.flags&1)==0 && !Bounds.Contains(m.point)) {
                    int msg=message.ToInt32(); MacroStep step=new MacroStep { X=m.point.X,Y=m.point.Y };
                    if(msg==0x200) { if(clock.ElapsedMilliseconds-lastMove<50)return CallNextHookEx(mouseHook,code,message,data); lastMove=clock.ElapsedMilliseconds; step.Kind=StepKind.MouseMove; }
                    else if(msg==0x20A) { step.Kind=StepKind.MouseWheel; step.Wheel=(short)(m.data>>16); }
                    else if(msg==0x201 || msg==0x204 || msg==0x207) { step.Kind=StepKind.MouseDown; step.Button=msg==0x204?1:msg==0x207?2:0; }
                    else if(msg==0x202 || msg==0x205 || msg==0x208) { step.Kind=StepKind.MouseUp; step.Button=msg==0x205?1:msg==0x208?2:0; }
                    else return CallNextHookEx(mouseHook,code,message,data);
                    Add(step);
                    if(step.Kind==StepKind.MouseDown)recordedButtons.Add(step.Button);
                    if(step.Kind==StepKind.MouseUp)recordedButtons.Remove(step.Button);
                }
            } catch { if(!ending)BeginInvoke(new Action(delegate { Finish(false); })); }
        }
        return CallNextHookEx(mouseHook,code,message,data);
    }
}
}
