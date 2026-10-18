namespace TouchLock.UI;

internal class AdaptiveForm : Form
{
    public AdaptiveForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MinimumSize = new Size(400, 300);
    }
    protected TableLayoutPanel Content(FlowLayoutPanel? footer = null)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = footer == null ? 1 : 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(24, 18, 24, 18) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(content); root.Controls.Add(scroll, 0, 0);
        if (footer != null)
        {
            footer.Dock = DockStyle.Fill; footer.AutoSize = true; footer.WrapContents = true; footer.Padding = new Padding(18, 8, 18, 12);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.Controls.Add(footer, 0, 1);
        }
        Controls.Add(root);
        content.SizeChanged += (_, _) =>
        {
            foreach (var label in content.Controls.OfType<Label>())
                label.MaximumSize = new Size(Math.Max(1, content.ClientSize.Width - content.Padding.Horizontal - label.Margin.Horizontal), 0);
        };
        return content;
    }
    protected static Label TextBlock(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 8) };
#if DEBUG
    internal static void VerifyLayout(Func<Form> create, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        foreach (float factor in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
        {
            using var form = create();
            form.ShowInTaskbar = false; form.Opacity = 0;
            form.Shown += (_, _) =>
            {
                form.Scale(new SizeF(factor, factor)); form.PerformLayout();
                Validate(form);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(directory, $"{name}-{factor * 100:0}.png"));
                form.Size = form.MinimumSize; form.PerformLayout(); Validate(form);
                form.Close();
            };
            form.ShowDialog();
            Console.WriteLine($"PASS layout scale simulation {name} {factor * 100:0}% and minimum size");
        }
        static void Validate(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible) continue;
                if (parent is not ScrollableControl { AutoScroll: true } && (child.Right > parent.ClientSize.Width + 2 || child.Left < -2))
                    throw new InvalidOperationException($"Horizontal overflow: {parent.GetType().Name}/{child.GetType().Name}");
                if (child is Button && (child.Height < TextRenderer.MeasureText(child.Text, child.Font).Height || child.Width < TextRenderer.MeasureText(child.Text, child.Font).Width))
                    throw new InvalidOperationException("Button text clipped: " + child.Text);
                Validate(child);
            }
        }
    }
#endif
}
