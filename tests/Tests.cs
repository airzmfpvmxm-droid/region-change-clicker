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
        Console.WriteLine("PASS: "+checks+" assertions (no real mouse input)");
    }
}
