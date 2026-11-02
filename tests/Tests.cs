using System;
using System.Drawing;
using RegionChangeClicker;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
class Tests {
    static int checks;
    static void Assert(bool condition,string name) { if(!condition)throw new Exception(name); checks++; }
    [STAThread] static void Main() {
        using(Bitmap a=new Bitmap(8,8)) using(Bitmap b=new Bitmap(8,8)) {
            using(Graphics g=Graphics.FromImage(a))g.Clear(Color.Black);
            using(Graphics g=Graphics.FromImage(b))g.Clear(Color.Black);
            Assert(!Detection.Find(a,b,0).HasValue,"Identical image");
            b.SetPixel(7,7,Color.FromArgb(10,0,0));
            Assert(Detection.Find(a,b,0)==new Point(7,7),"Bottom-right pixel");
            Assert(!Detection.Find(a,b,10).HasValue,"Tolerance boundary");
            Assert(Detection.Find(a,b,9).HasValue,"Above tolerance");
            b.SetPixel(2,1,Color.White);
            Assert(Detection.Find(a,b,0)==new Point(2,1),"First changed pixel");
            using(OneShot shot=new OneShot()) {
                int clicks=0; shot.Start(a);
                Assert(!shot.Check(a,0,delegate { clicks++; }),"No change no click");
                Assert(shot.Check(b,0,delegate(Point p) { Assert(!shot.Active,"Disarmed before click"); clicks++; }),"Change triggers");
                shot.Check(b,0,delegate { clicks++; }); Assert(clicks==1,"Exactly one click");
                shot.Start(a); shot.Dispose(); shot.Check(b,0,delegate { clicks++; }); Assert(clicks==1,"Manual stop");
                shot.Start(a);
                try { shot.Check(b,0,delegate { throw new Exception("Input failed"); }); } catch(Exception) { }
                Assert(!shot.Active,"Failure stays stopped");
                shot.Start(b); Assert(!shot.Check(b,0,delegate { clicks++; }),"Restart resets baseline");
            }
        }
        RepeatTests();
        using(RegionChangeClicker.Panel panel=new RegionChangeClicker.Panel()) {
            panel.CreateControl();
            Assert(panel.Controls.Count>20,"Integrated panel initializes");
        }
        SynchronizationContext.SetSynchronizationContext(null);
        MacroTests().GetAwaiter().GetResult();
        ScopeTests();
        MovementTests();
        Console.WriteLine("PASS: "+checks+" assertions (no real mouse input)");
    }
    static void MovementTests() {
        Rectangle desktop=new Rectangle(-1920,-1080,5760,2160); bool exact=true;
        for(int x=desktop.Left;x<desktop.Right;x++) {
            Point normalized=MouseMovement.Normalize(new Point(x,desktop.Top),desktop);
            if(desktop.Left+(long)normalized.X*desktop.Width/65536!=x)exact=false;
        }
        for(int y=desktop.Top;y<desktop.Bottom;y++) {
            Point normalized=MouseMovement.Normalize(new Point(desktop.Right-1,y),desktop);
            if(desktop.Top+(long)normalized.Y*desktop.Height/65536!=y)exact=false;
        }
        Assert(exact,"Absolute normalization maps every virtual desktop pixel exactly");
        bool outside=false;try{MouseMovement.Normalize(new Point(desktop.Right,0),desktop);}catch(ArgumentException){outside=true;}
        Assert(outside,"Absolute movement rejects outside coordinates");
        int fallback=0,confirmed=0;
        bool moved=MouseMovement.TryMove(new Point(1,2),delegate{return false;},delegate{fallback++;return true;},delegate{confirmed++;return true;});
        Assert(moved && fallback==1 && confirmed==1,"Failed SetCursorPos uses confirmed fallback");
        fallback=0;
        moved=MouseMovement.TryMove(Point.Empty,delegate{return true;},delegate{fallback++;return true;},delegate{return true;});
        Assert(moved && fallback==0,"Successful direct move skips fallback");
        confirmed=0;
        moved=MouseMovement.TryMove(Point.Empty,delegate{return false;},delegate{return false;},delegate{confirmed++;return true;});
        Assert(!moved && confirmed==0,"Blocked movement never reports success");
        confirmed=0;fallback=0;
        moved=MouseMovement.TryMove(Point.Empty,delegate{return true;},delegate{fallback++;return true;},delegate{confirmed++;return confirmed==2;});
        Assert(moved && fallback==1 && confirmed==2,"Direct move landing elsewhere uses verified fallback");
        moved=MouseMovement.TryMove(Point.Empty,delegate{return true;},delegate{return true;},delegate{return false;});
        Assert(!moved,"No click allowed if cursor never reaches destination");
        Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.Input))==(IntPtr.Size==8?40:28),"SendInput native structure size matches platform");
    }
    static void ScopeTests() {
        Rectangle area=new Rectangle(-100,20,200,100);
        MacroStep[] mixed={new MacroStep {Kind=StepKind.Key,Key=Keys.A},new MacroStep {Kind=StepKind.Text,Text="skip"},new MacroStep {Kind=StepKind.KeyDown,Key=Keys.ControlKey},new MacroStep {Kind=StepKind.Delay,Milliseconds=100},new MacroStep {Kind=StepKind.PositionClick,X=-100,Y=20},new MacroStep {Kind=StepKind.MouseMove,X=99,Y=119},new MacroStep {Kind=StepKind.MouseWheel,X=0,Y=50},new MacroStep {Kind=StepKind.MouseDown,X=0,Y=50},new MacroStep {Kind=StepKind.MouseUp,X=99,Y=119}};
        MacroStep[] filtered=MouseScope.Prepare(mixed,area);
        Assert(filtered.Length==6 && filtered[0].Kind==StepKind.Delay,"Mouse mode removes all keyboard events but keeps timing");
        foreach(MacroStep step in filtered)MouseScope.RequireStep(area,step,Point.Empty);
        Assert(filtered[1].X==-100 && filtered[2].X==99,"Negative coordinates and interior boundaries accepted");
        foreach(StepKind kind in new[]{StepKind.PositionClick,StepKind.MouseMove,StepKind.MouseWheel,StepKind.MouseDown,StepKind.MouseUp}) {
            bool rejected=false; try {MouseScope.Prepare(new[]{new MacroStep {Kind=kind,X=100,Y=50}},area);}catch(ArgumentException){rejected=true;}
            Assert(rejected,"Outside fixed mouse action rejected: "+kind);
        }
        bool outside=false;try{MouseScope.RequireStep(area,new MacroStep{Kind=StepKind.DetectedClick},new Point(0,120));}catch(ArgumentException){outside=true;}
        Assert(outside,"Detected click outside bottom boundary blocked");
        bool keyboard=false;try{MouseScope.RequireStep(area,new MacroStep{Kind=StepKind.Key,Key=Keys.Enter},Point.Empty);}catch(ArgumentException){keyboard=true;}
        Assert(keyboard,"Runtime guard blocks keyboard bypass");
        bool empty=false;try{MouseScope.Prepare(new[]{new MacroStep{Kind=StepKind.Text,Text="no mouse"}},area);}catch(ArgumentException){empty=true;}
        Assert(empty,"Keyboard-only macro rejected in mouse mode");
        MouseScope.RequireStep(area,new MacroStep{Kind=StepKind.DetectedClick},new Point(-100,20));
        Assert(true,"Inside detected click allowed");
    }
    static async Task MacroTests() {
        MacroStep[] source={ new MacroStep { Kind=StepKind.PositionClick,X=-20,Y=30,Button=2 },new MacroStep {Kind=StepKind.Delay,Milliseconds=500},new MacroStep {Kind=StepKind.Text,Text="한글 {a}+"},new MacroStep {Kind=StepKind.Key,Key=Keys.Control|Keys.A} };
        MacroStep[] armed=MacroRunner.Snapshot(source); source[2].Text="mutated";
        Assert(armed[2].Text=="한글 {a}+","Armed macro immutable snapshot");
        List<string> events=new List<string>(); int progress=0;
        await MacroRunner.RunAsync(armed,new Point(1,2),delegate(MacroStep step,Point p) { Assert(p==new Point(1,2),"Detection point passed to macro"); events.Add(step.Kind.ToString()); },CancellationToken.None,delegate(int i) { Assert(i==progress++,"Ordered progress"); },delegate(int ms,CancellationToken t) { Assert(ms==500,"Recorded delay honored"); events.Add("Delay"); return Task.FromResult(0); });
        Assert(string.Join(",",events)=="PositionClick,Delay,Text,Key","Mixed steps replay in order");
        using(CancellationTokenSource stop=new CancellationTokenSource()) {
            TaskCompletionSource<bool> entered=new TaskCompletionSource<bool>(); int count=0;
            Task run=MacroRunner.RunAsync(armed,Point.Empty,delegate {count++;},stop.Token,null,delegate(int ms,CancellationToken t) {entered.SetResult(true);return Task.Delay(600000,t);});
            await entered.Task; stop.Cancel(); bool canceled=false;
            try {await run;}catch(OperationCanceledException){canceled=true;}
            Assert(canceled && count==1,"Cancel during delay skips remaining inputs");
        }
        int sent=0; bool failed=false;
        try {await MacroRunner.RunAsync(armed,Point.Empty,delegate {sent++;throw new InvalidOperationException();},CancellationToken.None,null,Task.Delay);}catch(InvalidOperationException){failed=true;}
        Assert(failed && sent==1,"Input failure halts sequence");
        RecordingBuffer recording=new RecordingBuffer();
        MacroStep key=new MacroStep {Kind=StepKind.KeyDown,Key=Keys.LControlKey};
        recording.Add(key,250); key.Key=Keys.A;
        recording.Add(new MacroStep {Kind=StepKind.KeyUp,Key=Keys.LControlKey},400);
        MacroStep[] recorded=recording.Snapshot();
        Assert(recorded.Length==4 && recorded[0].Milliseconds==250 && recorded[2].Milliseconds==150,"Recorded timing between events");
        Assert(recorded[1].Key==Keys.LControlKey && recorded[3].Kind==StepKind.KeyUp,"Recorded keys cloned and releases preserved");
        string file=Path.Combine(Path.GetTempPath(),"region-macro-test-"+Guid.NewGuid().ToString("N")+".rcmacro");
        try {
            MacroStorage.Save(file,recorded,3); MacroFile loaded=MacroStorage.Load(file);
            Assert(loaded.Repeats==3 && loaded.Steps.Length==4 && loaded.Steps[1].Key==Keys.LControlKey,"Macro save/load round trip");
            MacroStorage.Save(file,armed,2); loaded=MacroStorage.Load(file);
            Assert(loaded.Steps[2].Text=="한글 {a}+" && loaded.Steps[0].Button==2,"Overwrite preserves Unicode and middle click");
            File.WriteAllText(file,"<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///missing'>]><MacroFile>&e;</MacroFile>");
            bool rejected=false; try{MacroStorage.Load(file);}catch(Exception){rejected=true;}
            Assert(rejected,"External XML entities rejected");
        } finally { if(File.Exists(file))File.Delete(file); }
        bool invalid=false; try{MacroRunner.Snapshot(new []{new MacroStep {Kind=StepKind.MouseDown,Button=5}});}catch(ArgumentException){invalid=true;}
        Assert(invalid,"Invalid recorded mouse button rejected");
        long clock=0;
        using(Bitmap a=new Bitmap(2,2))using(Bitmap b=new Bitmap(2,2))using(RepeatDetection repeat=new RepeatDetection(delegate{return clock;})) {
            b.SetPixel(0,0,Color.White); repeat.Start(a,2,1000); repeat.Check(b,0,delegate{});
            clock=5000;repeat.RestartCooldown();clock=5999;
            Assert(repeat.RemainingMilliseconds==1 && !repeat.Check(a,0,delegate{}),"Cooldown restarts after long macro");
            clock=6000; Assert(!repeat.Check(b,0,delegate{}) && !repeat.Waiting,"Post-macro rebase before next detection");
        }
    }
    static void RepeatTests() {
        long time=0; int clicks=0;
        using(Bitmap a=new Bitmap(8,8)) using(Bitmap b=new Bitmap(8,8)) using(Bitmap c=new Bitmap(8,8))
        using(RepeatDetection repeat=new RepeatDetection(delegate { return time; })) {
            using(Graphics g=Graphics.FromImage(a))g.Clear(Color.Black);
            using(Graphics g=Graphics.FromImage(b))g.Clear(Color.Black);
            using(Graphics g=Graphics.FromImage(c))g.Clear(Color.Black);
            b.SetPixel(3,2,Color.White); c.SetPixel(6,5,Color.Red);
            Action<Point> click=delegate(Point p) { clicks++; };
            repeat.Start(a,3,1000);
            Assert(!repeat.Check(a,0,click) && clicks==0,"Repeat unchanged baseline");
            Assert(repeat.Check(b,0,click) && clicks==1 && repeat.Completed==1,"Repeat first click");
            Assert(repeat.Active && repeat.Waiting && repeat.RemainingMilliseconds==1000,"Cooldown starts after click");
            time=999;
            Assert(!repeat.Check(c,0,click) && clicks==1,"New change ignored during cooldown");
            time=1000;
            Assert(!repeat.Check(b,0,click) && !repeat.Waiting,"Cooldown expiry rebases without click");
            time=5000;
            Assert(!repeat.Check(b,0,click) && clicks==1,"Persistent same change never repeats");
            Point hit=Point.Empty;
            Assert(repeat.Check(c,0,delegate(Point p) { hit=p; clicks++; }),"Next distinct transition clicks");
            Assert(hit==new Point(3,2) && repeat.Completed==2,"Actual changed pixel and count");
            time=6000; repeat.Check(c,0,click);
            Assert(repeat.Check(a,0,click) && repeat.Completed==3 && !repeat.Active,"Exact total limit stops");
            repeat.Check(b,0,click); Assert(clicks==3,"No click after limit");
            repeat.Start(a,1,1000); repeat.Check(b,0,click);
            Assert(!repeat.Active && repeat.Completed==1,"Default one-shot behavior");
            repeat.Start(a,2,0); repeat.Check(b,0,click);
            Assert(repeat.Waiting,"Zero cooldown still schedules post-click rebase");
            int before=clicks; repeat.Check(c,0,click); repeat.Check(c,0,click);
            Assert(clicks==before && !repeat.Waiting,"Zero cooldown skips click effects");
            repeat.Dispose(); repeat.Check(a,0,click);
            Assert(clicks==before && !repeat.Active,"Manual stop cancels repeat");
            repeat.Start(a,3,1000); bool failed=false;
            try { repeat.Check(b,0,delegate { throw new InvalidOperationException(); }); } catch(InvalidOperationException) { failed=true; }
            Assert(failed && !repeat.Active && repeat.Completed==0,"Failed click halts without counting");
            repeat.Start(a,2,1000); Assert(repeat.Completed==0 && !repeat.Waiting,"Restart clears count and cooldown");
            repeat.Check(b,0,click); repeat.Dispose(); time+=10000; repeat.Check(c,0,click);
            Assert(!repeat.Active,"Stop during cooldown stays stopped");
            bool invalid=false; try { repeat.Start(a,0,1000); } catch(ArgumentOutOfRangeException) { invalid=true; }
            Assert(invalid,"Invalid count rejected");
            invalid=false; try { repeat.Start(a,1,-1); } catch(ArgumentOutOfRangeException) { invalid=true; }
            Assert(invalid,"Invalid cooldown rejected");
        }
    }
}
