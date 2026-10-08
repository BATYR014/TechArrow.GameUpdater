using System.Diagnostics;
using TechArrow.GameUpdater.Services;
using TechArrow.GameUpdater.Infrastructure.Services;

internal static class LauncherChecks
{
    public static async Task RunAsync()
    {
        var testRoot = Path.Combine(Path.GetTempPath(),"TechArrowLauncherChecks-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar+"Release"+Path.DirectorySeparatorChar) ? "Release" : "Debug";
            var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../TechArrow.LauncherFixture/bin",configuration,"net8.0-windows"));
            for (var index=1;index<=7;index++)
            {
                var directory=Path.Combine(testRoot,index.ToString());
                if (index == 1) directory=Path.Combine(directory,"Portal","Binaries","Win64");
                Directory.CreateDirectory(directory);
                foreach(var file in Directory.EnumerateFiles(fixture)) File.Copy(file,Path.Combine(directory,Path.GetFileName(file)));
                var profile=LauncherProcessProfile.For(index);
                var executable=Path.Combine(directory,profile.Frontends[0]);
                if (!File.Exists(executable)) File.Copy(Path.Combine(directory,"EpicGamesLauncher.exe"),executable);
                Check(LauncherIdentification.Identify(executable, LauncherIdentification.Keys[index]) == LauncherIdentification.Keys[index], "selected executable identifies launcher " + index);
                if (index == 1) Check(LauncherIdentification.Identify(executable, "Steam") == "Epic", "wrong picker row routes Epic to its actual launcher");
                var unsupported = Path.Combine(directory, "unrelated-game.exe");
                File.Copy(executable, unsupported);
                Check(LauncherIdentification.Identify(unsupported, LauncherIdentification.Keys[index]) is null, "unrelated game is not accepted as launcher");
                using var process=Process.Start(new ProcessStartInfo(executable){ArgumentList={directory},UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}) ?? throw new Exception("Test fixture failed to start.");
                try
                {
                    var startup=Stopwatch.StartNew();
                    while(!File.Exists(Path.Combine(directory,"ready")) && startup.Elapsed<TimeSpan.FromSeconds(15))
                    { if(process.HasExited) throw new Exception("Fixture exited during startup.");await Task.Delay(50); }
                    Check(File.Exists(Path.Combine(directory,"ready")),"fixture window ready");
                    var selectedExecutable = executable;
                    if (index == 1)
                    {
                        selectedExecutable=Path.Combine(Path.GetDirectoryName(directory)!,"Win32","EpicGamesLauncher.exe");
                        Directory.CreateDirectory(Path.GetDirectoryName(selectedExecutable)!); File.Copy(executable, selectedExecutable);
                    }
                    var reader=new LauncherProcessReader(new(index,"isolated test",selectedExecutable));
                    Check(!reader.PauseWhenUserActive, "user activity does not pause maintenance by default");
                    var initial=reader.Read();await Task.Delay(450);var active=reader.Read();
                    Check(active.Sample.IsRunning && active.Sample.Reliable && active.Sample.HasMeasurement && active.Sample.IoBytesPerSecond>1024,"native counters detect active client for profile "+index);
                    var identity=active.Processes.Single(p=>p.Id==process.Id);
                    Check(LauncherProcessReader.Matches(identity) && !LauncherProcessReader.Matches(identity with {Started=identity.Started+1}) && !LauncherProcessReader.Matches(identity with {Path=executable+".other"}),"PID reuse and wrong executable path cannot identify exit target");
                    File.WriteAllText(Path.Combine(directory,"idle"),"");
                    var controlled=new ControlledReader(profile,identity);
                    var policy=ReadyPolicy(controlled);
                    int requests=0;
                    var disabled=await LauncherExitService.RequestAsync(controlled,policy,()=>false,5,default,(_,_)=>{requests++;return false;});
                    Check(disabled.Disabled && requests==0 && !process.HasExited,"disabled auto-close leaves client running");
                    controlled.Busy=true;
                    var busy=await LauncherExitService.RequestAsync(controlled,policy,()=>true,5,default,(_,_)=>{requests++;return false;});
                    Check(busy.Retry && requests==0 && !process.HasExited,"last-moment game or user activity cancels exit");
                    controlled.Busy=false;
                    using(var canceled=new CancellationTokenSource())
                    {
                        canceled.Cancel();bool rejected=false;
                        try {await LauncherExitService.RequestAsync(controlled,ReadyPolicy(controlled),()=>true,5,canceled.Token,(_,_)=>{requests++;return false;});}
                        catch(OperationCanceledException){rejected=true;}
                        Check(rejected && requests==0 && !process.HasExited,"stop token prevents exit request");
                    }
                    var result=await LauncherExitService.RequestAsync(controlled,ReadyPolicy(controlled),()=>true,5,default,(targets,token)=>{requests++;return LauncherExitService.RequestWindowExit(targets,token);});
                    Check(result.Exited,"exit result for fixture: "+result.Detail);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(result.Exited && requests==1 && File.ReadAllText(Path.Combine(directory,"closed"))=="graceful","native graceful close verified for profile "+index);
                }
                finally {if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
            }
            Console.WriteLine("PASS all seven launcher profiles, native activity, target identity, cancellation, game guard and graceful close. Real launcher queues/tray menus were not modified.");
        }
        finally {Directory.Delete(testRoot,true);}
    }
    private static LauncherIdlePolicy ReadyPolicy(ControlledReader reader)
    {
        var policy=new LauncherIdlePolicy(DateTimeOffset.UtcNow.AddHours(-1),60,30);
        policy.Evaluate(reader.Read().Sample with {Timestamp=DateTimeOffset.UtcNow.AddSeconds(-201)});
        policy.Evaluate(reader.Read().Sample with {Timestamp=DateTimeOffset.UtcNow.AddSeconds(-200)});
        return policy;
    }
    private sealed class ControlledReader(LauncherProcessProfile profile,LauncherProcessIdentity identity) : ILauncherProcessReader
    {
        public bool Busy {get;set;}
        public LauncherProcessProfile Profile=>profile;
        public LauncherProcessReading Read()=>new(new(DateTimeOffset.UtcNow,LauncherProcessReader.Matches(identity),true,Busy,true,identity.Id+":"+identity.Started,0,0,"test fixture"),[identity]);
    }
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
}
