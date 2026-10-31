using System;
using System.Drawing;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RegionChangeClicker;
class Tests {
    static int checks;
    static void Assert(bool condition,string name) { if(!condition)throw new Exception(name); checks++; }
    [STAThread] static void Main() {
        using(RegionChangeClicker.Panel panel=new RegionChangeClicker.Panel()) {
            Assert(panel.ClientSize.Width>=700,"Panel and macro editor initialize");
        }
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
        SynchronizationContext.SetSynchronizationContext(null);
        TestMacro().GetAwaiter().GetResult();
        Console.WriteLine("PASS: "+checks+" assertions (no real mouse or keyboard input)");
    }
    static async Task TestMacro() {
        MacroStep[] source={
            new MacroStep { Kind=StepKind.DetectedClick },
            new MacroStep { Kind=StepKind.Delay,Milliseconds=500 },
            new MacroStep { Kind=StepKind.PositionClick,X=-500,Y=320,RightButton=true },
            new MacroStep { Kind=StepKind.Text,Text="테스트 한글 + {문자}" },
            new MacroStep { Kind=StepKind.Key,Key=Keys.Control|Keys.A }
        };
        MacroStep[] steps=MacroRunner.Snapshot(source); source[3].Text="changed";
        Assert(steps[3].Text=="테스트 한글 + {문자}","Immutable armed snapshot");
        List<string> events=new List<string>(); Point hit=new Point(-20,55); int progress=0;
        Action<MacroStep,Point> input=delegate(MacroStep s,Point p) {
            Assert(p==hit,"Detected absolute coordinates preserved");
            events.Add(s.Kind.ToString());
            if(s.Kind==StepKind.PositionClick)Assert(s.X==-500 && s.Y==320 && s.RightButton,"Absolute coordinate and mouse button preserved");
            if(s.Kind==StepKind.Key)Assert(s.Key==(Keys.Control|Keys.A),"Key chord preserved");
        };
        await MacroRunner.RunAsync(steps,hit,input,CancellationToken.None,delegate(int i) { Assert(i==progress++,"Progress order"); },
            delegate(int ms,CancellationToken t) { Assert(ms==500,"Delay duration preserved"); events.Add("Delay"); return Task.FromResult(0); });
        Assert(string.Join(",",events)=="DetectedClick,Delay,PositionClick,Text,Key","Mixed macro order");
        Assert(progress==5,"All actions completed once");
        using(CancellationTokenSource stop=new CancellationTokenSource()) {
            int count=0; bool canceled=false;
            TaskCompletionSource<bool> entered=new TaskCompletionSource<bool>();
            Task run=MacroRunner.RunAsync(steps,hit,delegate { count++; },stop.Token,null,
                delegate(int ms,CancellationToken t) { entered.SetResult(true); return Task.Delay(600000,t); });
            await entered.Task; stop.Cancel();
            try { await run; } catch(OperationCanceledException) { canceled=true; }
            Assert(canceled && count==1,"Stop during delay skips remaining actions");
        }
        using(CancellationTokenSource stop=new CancellationTokenSource()) {
            stop.Cancel(); int count=0; bool canceled=false;
            try { await MacroRunner.RunAsync(steps,hit,delegate { count++; },stop.Token,null,Task.Delay); } catch(OperationCanceledException) { canceled=true; }
            Assert(canceled && count==0,"Stop before first action");
        }
        int sent=0; bool failed=false;
        try { await MacroRunner.RunAsync(steps,hit,delegate { sent++; throw new InvalidOperationException("Input failed"); },CancellationToken.None,null,Task.Delay); }
        catch(InvalidOperationException) { failed=true; }
        Assert(failed && sent==1,"Input failure halts remaining actions");
        int rejected=0;
        foreach(MacroStep invalid in new[] { new MacroStep { Kind=StepKind.Delay,Milliseconds=-1 },new MacroStep { Kind=StepKind.Delay,Milliseconds=600001 },new MacroStep { Kind=StepKind.Text,Text="" },new MacroStep { Kind=StepKind.Key,Key=Keys.ControlKey } }) {
            try { MacroRunner.Snapshot(new[] { invalid }); } catch(ArgumentException) { rejected++; }
        }
        Assert(rejected==4,"Invalid steps rejected before execution");
        bool empty=false; try { MacroRunner.Snapshot(new MacroStep[0]); } catch(ArgumentException) { empty=true; }
        Assert(empty,"Empty macro rejected");
        Assert(MacroStep.ValidKey(Keys.Enter) && MacroStep.ValidKey(Keys.Control|Keys.Shift|Keys.A),"Ordinary keys accepted");
    }
}
