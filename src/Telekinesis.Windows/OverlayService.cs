using System.Drawing;
using System.Windows.Forms;
using Telekinesis.Abstractions;

namespace Telekinesis.Windows;

/// <summary>
/// The X-ray overlay: a transparent, click-through, never-activating, always-on-top
/// window spanning the virtual desktop, drawing labeled boxes over screen regions.
/// Pure output — WS_EX_TRANSPARENT makes it invisible to the mouse, WS_EX_NOACTIVATE
/// keeps focus where it is, and it never appears in the taskbar. It runs on its own
/// UI thread so the backend stays fully async.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private readonly object _gate = new();
    private Thread? _thread;
    private OverlayForm? _form;

    public void Show(IReadOnlyList<HighlightRegion> regions, TimeSpan duration)
    {
        var form = EnsureStarted();
        form.BeginInvoke(() => form.SetRegions(regions, duration));
    }

    public void Clear()
    {
        OverlayForm? form;
        lock (_gate) form = _form;
        form?.BeginInvoke(() => form.SetRegions([], TimeSpan.Zero));
    }

    private OverlayForm EnsureStarted()
    {
        lock (_gate)
        {
            if (_form is not null) return _form;
            using var ready = new ManualResetEventSlim();
            OverlayForm? created = null;
            _thread = new Thread(() =>
            {
                // Per-thread PMv2 DPI awareness: the overlay must live in physical
                // pixels regardless of what the host process declared. Without it,
                // WinForms rescales the window and boxes land off-target.
                SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                var form = new OverlayForm();
                _ = form.Handle; // force handle creation before anyone Invokes on it
                created = form;
                ready.Set();
                Application.Run(form);
            })
            {
                IsBackground = true,
                Name = "telekinesis-overlay",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            _form = created ?? throw new InvalidOperationException("Overlay window failed to start.");
            return _form;
        }
    }

    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint value);

    public void Dispose()
    {
        OverlayForm? form;
        Thread? thread;
        lock (_gate) { form = _form; thread = _thread; _form = null; _thread = null; }
        if (form is not null)
        {
            try { form.BeginInvoke(form.Close); } catch { /* message loop already gone */ }
            thread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class OverlayForm : Form
    {
        // The key color becomes fully transparent on screen. Anything we paint in
        // other colors floats over the desktop.
        private static readonly Color Key = Color.FromArgb(1, 2, 3);

        private IReadOnlyList<HighlightRegion> _regions = [];
        private readonly System.Windows.Forms.Timer _clearTimer = new();

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Key;
            TransparencyKey = Key;
            DoubleBuffered = true;
            var vs = SendInputInjector.VirtualScreen();
            Bounds = new Rectangle(vs.X, vs.Y, vs.Width, vs.Height);
            _clearTimer.Tick += (_, _) => SetRegions([], TimeSpan.Zero);
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // WinForms rescales Bounds during handle creation; re-assert the raw
            // physical virtual-desktop rectangle through SetWindowPos, which takes
            // physical pixels and bypasses the scaling.
            var vs = SendInputInjector.VirtualScreen();
            const int SWP_NOACTIVATE = 0x0010;
            SetWindowPos(Handle, new nint(-1) /* HWND_TOPMOST */,
                vs.X, vs.Y, vs.Width, vs.Height, SWP_NOACTIVATE);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x0000_0080;   // no taskbar / alt-tab entry
                const int WS_EX_LAYERED = 0x0008_0000;      // required for the transparency key
                const int WS_EX_TRANSPARENT = 0x0000_0020;  // mouse passes straight through
                const int WS_EX_NOACTIVATE = 0x0800_0000;   // never takes focus
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void SetRegions(IReadOnlyList<HighlightRegion> regions, TimeSpan duration)
        {
            _regions = regions;
            _clearTimer.Stop();
            if (regions.Count > 0 && duration > TimeSpan.Zero)
            {
                _clearTimer.Interval = (int)Math.Clamp(duration.TotalMilliseconds, 1, int.MaxValue);
                _clearTimer.Start();
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            using var border = new Pen(Color.Lime, 3);
            using var fill = new SolidBrush(Color.Lime);
            using var textBrush = new SolidBrush(Color.Black);
            using var font = new Font("Segoe UI", 11, FontStyle.Bold);

            foreach (var r in _regions)
            {
                // Screen coordinates → client coordinates of this virtual-desktop-sized window.
                var rect = new Rectangle(r.Bounds.X - Left, r.Bounds.Y - Top, r.Bounds.Width, r.Bounds.Height);
                g.DrawRectangle(border, rect);
                if (string.IsNullOrEmpty(r.Label)) continue;

                var size = g.MeasureString(r.Label, font);
                var chip = new RectangleF(rect.X, rect.Y - size.Height - 2, size.Width + 8, size.Height + 2);
                if (chip.Y < 0) chip.Y = rect.Y + 2; // no room above — tuck inside
                g.FillRectangle(fill, chip);
                g.DrawString(r.Label, font, textBrush, chip.X + 4, chip.Y + 1);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _clearTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
