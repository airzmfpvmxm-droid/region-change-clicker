using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RegionChangeClicker {
sealed class PointPicker : Form {
    public Point Selected;
    public PointPicker() {
        FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual;
        Bounds=SystemInformation.VirtualScreen; TopMost=true; BackColor=Color.Black; Opacity=0.25;
        Cursor=Cursors.Cross; KeyPreview=true;
        KeyDown+=delegate(object s,KeyEventArgs e) { if(e.KeyCode==Keys.Escape) { DialogResult=DialogResult.Cancel; Close(); } };
        MouseDown+=delegate(object s,MouseEventArgs e) { if(e.Button==MouseButtons.Left) { Selected=PointToScreen(e.Location); DialogResult=DialogResult.OK; Close(); } };
    }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.DrawString("클릭할 좌표를 지정하세요 · Esc 취소",SystemFonts.MessageBoxFont,Brushes.White,20,20); }
}
public sealed class MacroEditor : UserControl {
    readonly ListBox list=new ListBox();
    readonly ComboBox kind=new ComboBox(), mouse=new ComboBox();
    readonly NumericUpDown x=new NumericUpDown(), y=new NumericUpDown(), wait=new NumericUpDown(), wheel=new NumericUpDown(), repeats=new NumericUpDown();
    readonly TextBox text=new TextBox(), keyBox=new TextBox();
    readonly Label xLabel=new Label(), yLabel=new Label(), waitLabel=new Label();
    Button pick;
    Keys chord=Keys.Enter;
    public bool Picking { get; private set; }
    public Func<Rectangle?> RecordingArea;
    public int Repeats { get { return (int)repeats.Value; } }
    public MacroEditor() {
        Size=new Size(696,410);
        Controls.Add(new Label { Text="변화 위치 클릭 후 실행할 매크로 · 추가/수정으로 적용",Bounds=new Rectangle(12,0,670,26) });
        list.Bounds=new Rectangle(12,30,332,270); list.HorizontalScrollbar=true; list.IntegralHeight=false;
        list.SelectedIndexChanged+=delegate { LoadSelected(); }; Controls.Add(list);
        kind.Bounds=new Rectangle(360,30,320,28); kind.DropDownStyle=ComboBoxStyle.DropDownList;
        kind.Items.AddRange(new object[] { "추가 변화 위치 클릭", "지정 좌표 클릭", "문자 입력", "키 / 조합키 입력", "지연 (ms)", "마우스 이동", "마우스 휠", "키 누르기", "키 떼기", "마우스 버튼 누르기", "마우스 버튼 떼기" });
        kind.SelectedIndexChanged+=delegate { ShowFields(); }; Controls.Add(kind);
        mouse.Bounds=new Rectangle(360,68,320,28); mouse.DropDownStyle=ComboBoxStyle.DropDownList;
        mouse.Items.AddRange(new object[] { "왼쪽 버튼", "오른쪽 버튼", "가운데 버튼" }); mouse.SelectedIndex=0; Controls.Add(mouse);
        xLabel.Text="X"; xLabel.Bounds=new Rectangle(360,110,20,25); Controls.Add(xLabel);
        yLabel.Text="Y"; yLabel.Bounds=new Rectangle(520,110,20,25); Controls.Add(yLabel);
        x.Bounds=new Rectangle(385,107,125,28); y.Bounds=new Rectangle(545,107,135,28);
        x.Minimum=y.Minimum=-100000; x.Maximum=y.Maximum=100000; Controls.Add(x); Controls.Add(y);
        pick=AddButton("화면에서 좌표 지정",360,146,320,delegate { Pick(); });
        text.Bounds=new Rectangle(360,68,320,125); text.Multiline=true; text.ScrollBars=ScrollBars.Vertical; text.MaxLength=2000; text.AcceptsReturn=true; Controls.Add(text);
        keyBox.Bounds=new Rectangle(360,68,320,28); keyBox.ReadOnly=true; keyBox.Text="Enter"; Controls.Add(keyBox);
        keyBox.PreviewKeyDown+=delegate(object s,PreviewKeyDownEventArgs e) { e.IsInputKey=true; };
        keyBox.KeyDown+=delegate(object s,KeyEventArgs e) {
            e.SuppressKeyPress=true;
            bool raw=kind.SelectedIndex==7 || kind.SelectedIndex==8;
            if(!raw && !MacroStep.ValidKey(e.KeyData))return;
            chord=kind.SelectedIndex==7 || kind.SelectedIndex==8 ? e.KeyCode : e.KeyData; keyBox.Text=new KeysConverter().ConvertToString(chord);
        };
        waitLabel.Text="지연 시간 (0~600000ms)"; waitLabel.Bounds=new Rectangle(360,68,320,25); Controls.Add(waitLabel);
        wait.Bounds=new Rectangle(360,103,320,28); wait.Maximum=600000; wait.Value=500; wait.Increment=100; Controls.Add(wait);
        wheel.Bounds=new Rectangle(360,180,320,28); wheel.Minimum=-12000; wheel.Maximum=12000; wheel.Value=120; wheel.Increment=120; Controls.Add(wheel);
        AddButton("추가",360,220,150,delegate { SaveStep(false); });
        AddButton("선택 동작 수정",520,220,160,delegate { SaveStep(true); });
        AddButton("위로",12,312,100,delegate { MoveStep(-1); });
        AddButton("아래로",124,312,100,delegate { MoveStep(1); });
        AddButton("삭제",236,312,108,delegate { int i=list.SelectedIndex; if(i<0)return; list.Items.RemoveAt(i); if(list.Items.Count>0)list.SelectedIndex=Math.Min(i,list.Items.Count-1); });
        Controls.Add(new Label { Text="키 입력은 위 칸을 누르고 원하는 키를 누르세요.\n문자는 클릭 등으로 초점이 잡힌 대상에 입력됩니다.",Bounds=new Rectangle(360,265,320,72) });
        AddButton("녹화",12,360,100,Record);
        AddButton("저장",124,360,100,SaveFile);
        AddButton("불러오기",236,360,108,LoadFile);
        Controls.Add(new Label { Text="목록 반복 (1~10000회)",Bounds=new Rectangle(360,365,205,28) });
        repeats.Bounds=new Rectangle(575,362,105,28); repeats.Minimum=1; repeats.Maximum=10000; repeats.Value=1; Controls.Add(repeats);
        kind.SelectedIndex=1;
    }
    Button AddButton(string label,int bx,int by,int width,Action action) {
        Button b=new Button { Text=label,Bounds=new Rectangle(bx,by,width,34) }; b.Click+=delegate { action(); }; Controls.Add(b); return b;
    }
    void ShowFields() {
        int k=kind.SelectedIndex; mouse.Visible=k==0 || k==1 || k==9 || k==10;
        x.Visible=y.Visible=xLabel.Visible=yLabel.Visible=pick.Visible=k==1 || k==5 || k==6 || k==9 || k==10;
        text.Visible=k==2; keyBox.Visible=k==3 || k==7 || k==8; wait.Visible=waitLabel.Visible=k==4; wheel.Visible=k==6;
    }
    void LoadSelected() {
        MacroStep s=list.SelectedItem as MacroStep; if(s==null)return;
        kind.SelectedIndex=(int)s.Kind; mouse.SelectedIndex=s.RightButton?1:s.Button;
        x.Value=s.X; y.Value=s.Y; wait.Value=s.Milliseconds; text.Text=s.Text;
        chord=s.Key; wheel.Value=s.Wheel; keyBox.Text=new KeysConverter().ConvertToString(chord); ShowFields();
    }
    void SaveStep(bool update) {
        try {
            if(update && list.SelectedIndex<0)throw new ArgumentException("수정할 동작을 선택하세요.");
            if(!update && list.Items.Count>=2000)throw new ArgumentException("동작은 최대 2000개입니다.");
            MacroStep s=new MacroStep { Kind=(StepKind)kind.SelectedIndex, X=(int)x.Value,Y=(int)y.Value,Milliseconds=(int)wait.Value,RightButton=mouse.SelectedIndex==1,Button=mouse.SelectedIndex,Wheel=(int)wheel.Value,Text=text.Text,Key=chord };
            s.Validate();
            if(update)list.Items[list.SelectedIndex]=s;
            else { list.Items.Add(s); list.SelectedIndex=list.Items.Count-1; }
        } catch(ArgumentException ex) { MessageBox.Show(this,ex.Message,"동작 설정",MessageBoxButtons.OK,MessageBoxIcon.Information); }
    }
    void MoveStep(int delta) {
        int i=list.SelectedIndex, j=i+delta; if(i<0 || j<0 || j>=list.Items.Count)return;
        object item=list.Items[i]; list.Items.RemoveAt(i); list.Items.Insert(j,item); list.SelectedIndex=j;
    }
    void Pick() {
        Form owner=FindForm(); Picking=true; owner.Hide();
        try { using(PointPicker picker=new PointPicker()) if(picker.ShowDialog()==DialogResult.OK) { Point p=picker.Selected; x.Value=p.X; y.Value=p.Y; } }
        finally { owner.Show(); owner.Activate(); Picking=false; }
    }
    public MacroStep[] Snapshot() {
        List<MacroStep> steps=new List<MacroStep>(); foreach(MacroStep s in list.Items)steps.Add(s);
        return MacroRunner.Snapshot(steps);
    }
    void Replace(MacroStep[] steps) { list.Items.Clear(); foreach(MacroStep step in steps)list.Items.Add(step); if(list.Items.Count>0)list.SelectedIndex=0; }
    void Record() {
        Rectangle? allowed=null;
        try { if(RecordingArea!=null)allowed=RecordingArea(); }
        catch(ArgumentException ex) { MessageBox.Show(this,ex.Message,"녹화 영역"); return; }
        Form owner=FindForm(); Picking=true; owner.Hide();
        try { using(RecorderDialog dialog=new RecorderDialog(allowed)) if(dialog.ShowDialog()==DialogResult.OK)Replace(dialog.Result); }
        finally { owner.Show(); owner.Activate(); Picking=false; }
    }
    void SaveFile() {
        try { MacroStep[] steps=Snapshot(); using(SaveFileDialog dialog=new SaveFileDialog { Filter="영역 감지 매크로 (*.rcmacro)|*.rcmacro",DefaultExt="rcmacro",FileName="내 매크로.rcmacro" }) if(dialog.ShowDialog(this)==DialogResult.OK)MacroStorage.Save(dialog.FileName,steps,Repeats); }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"저장 실패"); }
    }
    void LoadFile() {
        try { using(OpenFileDialog dialog=new OpenFileDialog { Filter="영역 감지 매크로 (*.rcmacro)|*.rcmacro" }) if(dialog.ShowDialog(this)==DialogResult.OK) { MacroFile file=MacroStorage.Load(dialog.FileName); Replace(file.Steps); repeats.Value=file.Repeats; } }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"불러오기 실패"); }
    }
}
}
