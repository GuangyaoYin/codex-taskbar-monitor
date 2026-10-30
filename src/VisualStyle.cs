using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CodexTaskbarMonitor
{
    public sealed class QuietMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Func<bool> isDark;
        public QuietMenuRenderer(Func<bool> darkTheme) { isDark=darkTheme; }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        { e.Graphics.Clear(MonitorPalette.For(isDark()).Surface); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if(!e.Item.Selected&&!e.Item.Pressed)return;
            MonitorPalette p=MonitorPalette.For(isDark());
            VisualStyle.RoundedFill(e.Graphics,new RectangleF(3,1,e.Item.Width-6,e.Item.Height-2),3,p.HoverSurface);
            using(Brush brush=new SolidBrush(p.Accent))e.Graphics.FillRectangle(brush,3,5,2,Math.Max(1,e.Item.Height-10));
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            MonitorPalette p=MonitorPalette.For(isDark());
            float size=e.TextFont.SizeInPoints*e.Graphics.DpiY/72;
            float height=MixedText.Measure(e.Graphics,e.Text,size).Height;
            MixedText.Draw(e.Graphics,e.Text,size,e.Item.Enabled?p.Foreground:p.Muted,e.TextRectangle.Left,e.TextRectangle.Top+(e.TextRectangle.Height-height)/2,false);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using(Pen pen=new Pen(MonitorPalette.For(isDark()).Divider))e.Graphics.DrawLine(pen,25,e.Item.Height/2,e.Item.Width-8,e.Item.Height/2);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            float x=e.ImageRectangle.Left+3,y=e.ImageRectangle.Top+e.ImageRectangle.Height/2;
            using(Pen pen=new Pen(MonitorPalette.For(isDark()).Accent,1.6f))
            { pen.LineJoin=LineJoin.Round;e.Graphics.DrawLines(pen,new PointF[]{new PointF(x,y),new PointF(x+3,y+3),new PointF(x+9,y-4)}); }
        }
    }

    public sealed class MonitorPalette
    {
        public Color Surface, HoverSurface, Foreground, Muted, Track, Divider, Accent, Hardware, Warning, Danger;
        public static MonitorPalette For(bool dark)
        {
            return dark ? new MonitorPalette {
                Surface=Color.FromArgb(35,38,43), HoverSurface=Color.FromArgb(42,46,52),
                Foreground=Color.FromArgb(237,241,246), Muted=Color.FromArgb(158,169,183),
                Track=Color.FromArgb(65,72,81), Divider=Color.FromArgb(65,72,81),
                Accent=Color.FromArgb(116,190,173), Hardware=Color.FromArgb(135,164,199),
                Warning=Color.FromArgb(233,187,101), Danger=Color.FromArgb(231,133,133)
            } : new MonitorPalette {
                Surface=Color.FromArgb(237,240,243), HoverSurface=Color.FromArgb(245,247,249),
                Foreground=Color.FromArgb(39,48,61), Muted=Color.FromArgb(107,119,135),
                Track=Color.FromArgb(211,218,226), Divider=Color.FromArgb(210,217,225),
                Accent=Color.FromArgb(61,135,119), Hardware=Color.FromArgb(94,125,164),
                Warning=Color.FromArgb(166,116,34), Danger=Color.FromArgb(183,73,73)
            };
        }
    }

    public static class VisualStyle
    {
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath path=new GraphicsPath();
            float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
            if(d<=0) { path.AddRectangle(r); return path; }
            path.AddArc(r.Left,r.Top,d,d,180,90);
            path.AddArc(r.Right-d,r.Top,d,d,270,90);
            path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);
            path.AddArc(r.Left,r.Bottom-d,d,d,90,90);
            path.CloseFigure();return path;
        }
        public static void RoundedFill(Graphics g, RectangleF r, float radius, Color color)
        {
            if(r.Width<=0 || r.Height<=0) return;
            using(GraphicsPath path=Round(r,radius))
            using(Brush brush=new SolidBrush(color)) g.FillPath(brush,path);
        }
        public static void RightText(Graphics g,string text,float size,Color color,float right,float top,bool bold)
        {
            float width=MixedText.Measure(g,text,size).Width;
            if(bold)
            {
                // Measure the bold face too, so 100% remains inside the same fixed column.
                using(Font f=new Font("Times New Roman",size,FontStyle.Bold,GraphicsUnit.Pixel))
                using(StringFormat format=(StringFormat)StringFormat.GenericTypographic.Clone())
                { format.FormatFlags|=StringFormatFlags.MeasureTrailingSpaces|StringFormatFlags.NoWrap; width=g.MeasureString(text,f,Int32.MaxValue,format).Width; }
            }
            MixedText.Draw(g,text,size,color,right-width,top,bold);
        }
        public static void Icon(Graphics g,int kind,float x,float y,float size,Color color,float scale)
        {
            using(Pen p=new Pen(color,Math.Max(1,0.85f*scale)))
            {
                p.StartCap=LineCap.Round;p.EndCap=LineCap.Round;p.LineJoin=LineJoin.Round;
                if(kind==0) // CPU
                {
                    g.DrawRectangle(p,x+size*.22f,y+size*.22f,size*.56f,size*.56f);
                    for(int i=0;i<3;i++)
                    {
                        float t=size*(.32f+i*.18f);
                        g.DrawLine(p,x+t,y,x+t,y+size*.19f);g.DrawLine(p,x+t,y+size*.81f,x+t,y+size);
                        g.DrawLine(p,x,y+t,x+size*.19f,y+t);g.DrawLine(p,x+size*.81f,y+t,x+size,y+t);
                    }
                }
                else if(kind==1) // Memory module
                {
                    g.DrawRectangle(p,x,y+size*.18f,size,size*.6f);
                    for(int i=0;i<3;i++)
                    {
                        float t=x+size*(.2f+i*.3f);
                        g.DrawLine(p,t,y+size*.34f,t,y+size*.6f);
                        g.DrawLine(p,t,y+size*.8f,t,y+size);
                    }
                }
                else if(kind==2) // GPU card
                {
                    g.DrawRectangle(p,x,y+size*.15f,size*.9f,size*.65f);
                    g.DrawEllipse(p,x+size*.2f,y+size*.3f,size*.34f,size*.34f);
                    g.DrawLine(p,x+size*.93f,y,x+size*.93f,y+size);
                    g.DrawLine(p,x+size*.2f,y+size*.85f,x+size*.55f,y+size*.85f);
                }
                else // Terminal, no dependency on an icon font
                {
                    g.DrawLines(p,new PointF[]{new PointF(x,y+size*.15f),new PointF(x+size*.35f,y+size*.48f),new PointF(x,y+size*.8f)});
                    g.DrawLine(p,x+size*.45f,y+size*.8f,x+size*.95f,y+size*.8f);
                }
            }
        }
        public static void Progress(Graphics g,RectangleF rectangle,double? percent,Color track,Color fill)
        {
            RoundedFill(g,rectangle,rectangle.Height/2,track);
            if(percent.HasValue && !Double.IsNaN(percent.Value) && !Double.IsInfinity(percent.Value))
            {
                float width=(float)(rectangle.Width*Math.Max(0,Math.Min(100,percent.Value))/100);
                if(width>0) RoundedFill(g,new RectangleF(rectangle.X,rectangle.Y,width,rectangle.Height),rectangle.Height/2,fill);
            }
        }
    }

    public static class CompactPainter
    {
        public const int PreferredWidthDip = 344;
        public const int PreferredHeightDip = 38;

        public static void Draw(Graphics g,Rectangle bounds,float s,bool dark,bool hover,HardwareSnapshot hardware,GpuSnapshot gpu,CodexSnapshot quota,NetworkSnapshot network=null)
        {
            MonitorPalette p=MonitorPalette.For(dark);
            g.Clear(hover?p.HoverSurface:p.Surface);
            g.SmoothingMode=SmoothingMode.AntiAlias;
            g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Metric(g,0,"CPU",hardware==null?null:hardware.CpuPercent,8*s,s,p);
            Metric(g,1,"RAM",hardware==null?null:hardware.MemoryPercent,84*s,s,p);
            Metric(g,2,"GPU",gpu==null?null:gpu.UtilizationPercent,160*s,s,p);
            // Keep the original 240-DIP panel intact for compact callers; the
            // live window appends two network rows without changing its height.
            if(bounds.Width>=320*s) Network(g,network,s,p);
            VisualStyle.Icon(g,3,8*s,25*s,8*s,p.Accent,s);
            MixedText.Draw(g,"Codex",11.5f*s,p.Foreground,21*s,22*s,true);
            QuotaBucket bucket=DisplayText.MainBucket(quota);
            bool stale=quota!=null&&(quota.IsStale||quota.Status!=CodexStatus.Ready);
            if(bucket==null||bucket.Windows.Count==0)
            {
                string text=quota==null?"查询中…":quota.Status==CodexStatus.Missing?"未安装":quota.Status==CodexStatus.LoginRequired?"请登录":quota.Status==CodexStatus.Ready?"暂无额度数据":"查询失败";
                MixedText.Draw(g,text,11*s,stale?p.Warning:p.Muted,76*s,22*s,false);
                return;
            }
            MixedText.Draw(g,"剩余",9*s,p.Muted,54*s,23*s,false);
            float left=74*s,right=232*s;
            if(stale)
            {
                MixedText.Draw(g,"旧",10*s,p.Warning,222*s,23*s,false);
                right=216*s;
            }
            int count=bucket.Windows.Count;
            float spacing=9*s;
            float cell=(right-left-spacing*(count-1))/count;
            for(int i=0;i<count;i++)
            {
                QuotaWindow window=bucket.Windows[i];
                float x=left+i*(cell+spacing);
                string label=DisplayText.WindowLabel(window.WindowDurationMins);
                float labelSize=10.5f*s;
                float labelWidth=MixedText.Measure(g,label,labelSize).Width;
                float maximumLabel=Math.Max(11*s,cell*.32f);
                if(labelWidth>maximumLabel) labelSize*=maximumLabel/labelWidth;
                labelWidth=MixedText.Measure(g,label,labelSize).Width;
                MixedText.Draw(g,label,labelSize,p.Muted,x,23*s,false);
                double? remaining=DisplayText.RemainingPercent(window.UsedPercent);
                string value=DisplayText.Percent(remaining);
                Color fill=stale?p.Warning:remaining<=10?p.Danger:remaining<=25?p.Warning:p.Accent;
                float valueWidth=MixedText.Measure(g,value,12*s).Width+2*s;
                VisualStyle.RightText(g,value,12*s,remaining<=25?fill:p.Foreground,x+cell,21*s,true);
                float trackLeft=x+labelWidth+6*s;
                float trackRight=x+cell-valueWidth-6*s;
                if(trackRight>trackLeft)
                    VisualStyle.Progress(g,new RectangleF(trackLeft,28*s,trackRight-trackLeft,3*s),remaining,p.Track,fill);
            }
        }

        private static void Metric(Graphics g,int icon,string label,double? value,float x,float s,MonitorPalette p)
        {
            VisualStyle.Icon(g,icon,x,5*s,8*s,p.Muted,s);
            MixedText.Draw(g,label,10*s,p.Muted,x+12*s,2.5f*s,false);
            VisualStyle.RightText(g,DisplayText.Percent(value),12.5f*s,p.Foreground,x+70*s,1*s,true);
            VisualStyle.Progress(g,new RectangleF(x+12*s,16*s,58*s,1.5f*s),value,p.Track,p.Hardware);
        }

        private static void Network(Graphics g,NetworkSnapshot network,float s,MonitorPalette p)
        {
            using(Pen divider=new Pen(p.Divider,Math.Max(1,0.65f*s)))
                g.DrawLine(divider,244*s,4*s,244*s,34*s);
            double? download=network==null?null:network.DownloadBytesPerSecond;
            double? upload=network==null?null:network.UploadBytesPerSecond;
            TransferArrow(g,true,254*s,5*s,8*s,p.Muted,s);
            TransferArrow(g,false,254*s,25*s,8*s,p.Muted,s);
            VisualStyle.RightText(g,DisplayText.Speed(download),11.5f*s,download.HasValue?p.Foreground:p.Muted,336*s,1*s,false);
            VisualStyle.RightText(g,DisplayText.Speed(upload),11.5f*s,upload.HasValue?p.Foreground:p.Muted,336*s,21*s,false);
        }

        private static void TransferArrow(Graphics g,bool download,float x,float y,float size,Color color,float scale)
        {
            using(Pen pen=new Pen(color,Math.Max(1,0.9f*scale)))
            {
                pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
                float center=x+size*0.5f;
                float head=download?y+size:y;
                float tail=download?y:y+size;
                float shoulder=download?y+size*0.58f:y+size*0.42f;
                g.DrawLine(pen,center,tail,center,head);
                g.DrawLines(pen,new PointF[]{new PointF(x+size*0.1f,shoulder),new PointF(center,head),new PointF(x+size*0.9f,shoulder)});
            }
        }
    }
}
