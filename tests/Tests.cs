using System;
using System.Drawing;
using RegionChangeClicker;
class Tests {
    static int checks;
    static void Assert(bool condition,string name) { if(!condition)throw new Exception(name); checks++; }
    static void Main() {
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
        Console.WriteLine("PASS: "+checks+" assertions (no real mouse input)");
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
