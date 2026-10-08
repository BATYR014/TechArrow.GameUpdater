using System.Drawing;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) return;
        var directory = args[0];
        using var window = new TestWindow(directory);
        Application.Run(window);
    }
    private sealed class TestWindow : Form
    {
        private readonly System.Windows.Forms.Timer _timer = new() {Interval=100};
        private readonly string _directory;
        private readonly byte[] _bytes = new byte[65536];
        public TestWindow(string directory)
        {
            _directory=directory;
            Text="TechArrow isolated launcher test"; ShowInTaskbar=false;
            StartPosition=FormStartPosition.Manual; Location=new Point(-32000,-32000); Size=new Size(200,100);
            _timer.Tick += (_, _) => { if (!File.Exists(Path.Combine(_directory,"idle"))) File.WriteAllBytes(Path.Combine(_directory,"activity"),_bytes); };
            Shown += (_, _) => { File.WriteAllText(Path.Combine(_directory,"ready"),Environment.ProcessId.ToString()); _timer.Start(); };
            FormClosing += (_, _) => { _timer.Stop(); File.WriteAllText(Path.Combine(_directory,"closed"),"graceful"); };
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get {var p=base.CreateParams;p.ExStyle|=0x08000000;return p;} }
        protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
    }
}
