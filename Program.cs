// walkingpad - set the speed of a Kingsmith WalkingPad over BLE from the command line.
// Protocol from QWalkingPad (https://github.com/DorianRudolph/QWalkingPad), Protocol.cpp / MainWindow.cpp:
//   service 0xFE00, notify characteristic 0xFE01, write characteristic 0xFE02
//   short command: F7 A2 <key> <value> <sum(A2,key,value)> FD
//   key 0 = query state, 1 = set speed (0.1 km/h, 0 stops the belt), 2 = set mode, 4 = start belt
//   long command:  F7 A6 <key> 00 <24-bit value> <sum> FD  (key 0 = query params)

using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Microsoft.Win32;

static class Program
{
    const byte ModeAuto = 0, ModeManual = 1, ModeSleep = 2;
    static readonly Guid ServiceUuid = ShortUuid(0xfe00);
    static readonly Guid NotifyUuid = ShortUuid(0xfe01);
    static readonly Guid WriteUuid = ShortUuid(0xfe02);
    static readonly TimeSpan CommandGap = TimeSpan.FromMilliseconds(700);
    static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WalkingPadCtl", "address.txt");

    static bool verbose;

    static Guid ShortUuid(ushort id) => new($"0000{id:x4}-0000-1000-8000-00805f9b34fb");

    record Info(byte State, byte Speed, byte Mode, uint Time, uint Distance, uint Steps)
    {
        // per QWalkingPad: belt is running unless state is 0 (stopped) or 5 (standby)
        public bool Running => State != 0 && State != 5;
        public override string ToString() =>
            $"state={State} speed={Speed / 10.0:0.0} km/h mode={ModeName(Mode)} time={Time}s distance={Distance * 10}m steps={Steps}";
    }

    record Params(byte GoalType, uint Goal, byte Regulate, byte MaxSpeed, byte StartSpeed, byte StartMode,
                  byte Sensitivity, byte Display, byte Lock, byte Unit);

    static string ModeName(byte m) => m switch { ModeAuto => "auto", ModeManual => "manual", ModeSleep => "sleep", _ => m.ToString() };

    static string StateName(byte s) => s switch
    {
        0 => "stopped", 1 => "running", 5 => "standby", >= 6 and <= 9 => "starting", _ => $"unknown ({s})"
    };

    static string SensitivityName(byte s) => s switch { 1 => "high", 2 => "medium", 3 => "low", _ => s.ToString() };

    static bool jsonOutput;

    // connection progress: stdout normally, stderr in -Json mode so stdout stays parseable
    static void Status(string s) { if (jsonOutput) Console.Error.WriteLine(s); else Console.WriteLine(s); }

    static async Task<int> Read(Pad pad, Info info)
    {
        var p = await pad.QueryParams();
        if (jsonOutput)
        {
            var obj = new
            {
                state = StateName(info.State), stateCode = info.State, running = info.Running,
                speed = info.Speed / 10.0, mode = ModeName(info.Mode),
                timeSeconds = info.Time, distanceMeters = info.Distance * 10, steps = info.Steps,
                maxSpeed = p?.MaxSpeed / 10.0, startSpeed = p?.StartSpeed / 10.0,
                autoStart = p == null ? (bool?)null : p.StartMode != 0,
                sensitivity = p == null ? null : SensitivityName(p.Sensitivity),
                locked = p == null ? (bool?)null : p.Lock != 0,
                unit = p == null ? null : p.Unit == 0 ? "metric" : "imperial",
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(obj,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"State:       {StateName(info.State)}");
        Console.WriteLine($"Speed:       {info.Speed / 10.0:0.0} km/h");
        Console.WriteLine($"Mode:        {ModeName(info.Mode)}");
        Console.WriteLine($"Time:        {TimeSpan.FromSeconds(info.Time):h\\:mm\\:ss}");
        Console.WriteLine($"Distance:    {info.Distance / 100.0:0.00} km");
        Console.WriteLine($"Steps:       {info.Steps}");
        if (p != null)
        {
            Console.WriteLine($"Max speed:   {p.MaxSpeed / 10.0:0.0} km/h");
            Console.WriteLine($"Start speed: {p.StartSpeed / 10.0:0.0} km/h");
            Console.WriteLine($"Auto start:  {(p.StartMode != 0 ? "on" : "off")}");
            Console.WriteLine($"Sensitivity: {SensitivityName(p.Sensitivity)}");
            Console.WriteLine($"Locked:      {(p.Lock != 0 ? "yes" : "no")}");
            Console.WriteLine($"Unit:        {(p.Unit == 0 ? "metric" : "imperial")}");
        }
        else Console.Error.WriteLine("(pad did not report its settings)");
        return 0;
    }

    static int Main(string[] args)
    {
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception e) { Console.Error.WriteLine($"Error: {e.Message}"); return 1; }
    }

    static void Usage() => Console.Error.WriteLine(
        """
        Usage: walkingpad [-Speed <n>] [-Json] [-Address AA:BB:CC:DD:EE:FF] [-Scan] [-Verbose]
          -Speed n    speed in 0.1 km/h (e.g. 35 = 3.5 km/h); 0 stops the belt
                      omit to just read and print the pad's state and settings
          -Json       (read mode) print the state as JSON
          -Address    WalkingPad BLE address (default: cached / QWalkingPad's last device / scan)
          -Scan       ignore cached address and scan for the pad
          -Verbose    print protocol details
        """);

    static async Task<int> Run(string[] args)
    {
        int? speed = null; ulong? address = null; bool forceScan = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].TrimStart('-', '/').ToLowerInvariant();
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
            switch (a)
            {
                case "speed" or "s": speed = int.Parse(Next()); break;
                case "address" or "a": address = ParseAddress(Next()); break;
                case "scan": forceScan = true; break;
                case "verbose" or "v": verbose = true; break;
                case "json" or "j": jsonOutput = true; break;
                case "help" or "h" or "?": Usage(); return 0;
                default:
                    if (speed == null && int.TryParse(args[i], out var p)) { speed = p; break; }
                    Usage(); return 2;
            }
        }

        if (speed is not int target)
        {
            using var reader = await Pad.Connect(address, forceScan);
            var current = await reader.InitialQuery() ?? throw new Exception("WalkingPad did not report its state.");
            return await Read(reader, current);
        }
        if (jsonOutput) throw new ArgumentException("-Json is only supported in read mode (without -Speed).");
        if (target < 0 || target > 120) throw new ArgumentException("Speed must be 0..120 (units of 0.1 km/h).");
        if (target is > 0 and < 5)
            throw new ArgumentException($"Speed is in 0.1 km/h units and the minimum belt speed is 0.5 km/h. Did you mean -Speed {target * 10}?");

        using var pad = await Pad.Connect(address, forceScan);
        var info = await pad.InitialQuery() ?? throw new Exception("WalkingPad did not report its state.");
        Console.WriteLine($"Current: {info}");

        if (target == 0)
        {
            if (!info.Running && info.Speed == 0) { Console.WriteLine("Already stopped."); return 0; }
            // Setting speed 0 only drops the belt to its minimum (0.8 km/h). Like QWalkingPad's Stop
            // button, send the start command, which toggles the belt off while it is running.
            await pad.Send(Cmd(4, 1));
            info = await pad.WaitFor(i => !i.Running, TimeSpan.FromSeconds(10));
            if (info != null) { Console.WriteLine($"Stopped: {info}"); return 0; }
            Console.Error.WriteLine("Stop command sent but the pad did not confirm it stopped.");
            return 1;
        }

        if (info.Running && info.Mode == ModeManual && info.Speed == target)
        {
            Console.WriteLine($"Already at {target / 10.0:0.0} km/h.");
            return 0;
        }

        var parms = await pad.QueryParams();
        if (parms is { } p2 && p2.MaxSpeed > 0 && target > p2.MaxSpeed)
            throw new ArgumentException($"Speed {target / 10.0:0.0} km/h exceeds pad max speed {p2.MaxSpeed / 10.0:0.0} km/h.");

        if (info.Mode != ModeManual)
        {
            Console.WriteLine($"Switching mode {ModeName(info.Mode)} -> manual");
            await pad.Send(Cmd(2, ModeManual));
            info = await pad.WaitFor(i => i.Mode == ModeManual, TimeSpan.FromSeconds(5)) ?? info;
        }

        if (!info.Running)
        {
            Console.WriteLine("Starting belt...");
            await pad.Send(Cmd(4, 1));
            // the pad counts down ~3 s before the belt moves (state 9 while starting)
            info = await pad.WaitFor(i => i.State == 1, TimeSpan.FromSeconds(15))
                   ?? throw new Exception("Belt did not start.");
        }

        await pad.Send(Cmd(1, (byte)target));
        info = await pad.WaitFor(i => i.Speed == target, TimeSpan.FromSeconds(10));
        if (info != null) Console.WriteLine($"Set: {info}");
        else Console.WriteLine($"Speed {target / 10.0:0.0} km/h sent (pad still ramping / not confirmed).");
        return 0;
    }

    static byte[] Cmd(byte key, byte value) => [0xf7, 0xa2, key, value, (byte)(0xa2 + key + value), 0xfd];

    static byte[] CmdInt(byte key, uint value)
    {
        byte[] d = [0xf7, 0xa6, key, 0, (byte)(value >> 16), (byte)(value >> 8), (byte)value, 0, 0xfd];
        for (int i = 1; i < 7; i++) d[7] += d[i];
        return d;
    }

    static ulong ParseAddress(string s) => Convert.ToUInt64(s.Replace(":", "").Replace("-", ""), 16);
    static string FormatAddress(ulong a) => string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (40 - 8 * i)) & 0xff).ToString("X2")));

    static void Log(string s) { if (verbose) Console.Error.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] {s}"); }

    sealed class Pad : IDisposable
    {
        BluetoothLEDevice device = null!;
        GattDeviceService service = null!;
        GattCharacteristic notifyChar = null!, writeChar = null!;
        GattWriteOption writeOption;
        DateTime lastSend = DateTime.MinValue;
        Info? lastInfo;
        Params? lastParams;
        readonly object gate = new();
        TaskCompletionSource infoSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource paramsSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Pad> Connect(ulong? explicitAddress, bool forceScan)
        {
            var candidates = new List<ulong>();
            if (explicitAddress is { } ea) candidates.Add(ea);
            else if (!forceScan)
            {
                if (ReadCachedAddress() is { } c) candidates.Add(c);
                if (ReadQWalkingPadAddress() is { } q && !candidates.Contains(q)) candidates.Add(q);
            }

            foreach (var addr in candidates)
            {
                Log($"Trying {FormatAddress(addr)}");
                var pad = await TryConnect(addr);
                if (pad != null) return pad;
            }
            if (explicitAddress != null) throw new Exception($"Could not connect to {FormatAddress(explicitAddress.Value)}.");

            Status("Scanning for WalkingPad...");
            var found = await Scan(TimeSpan.FromSeconds(15)) ?? throw new Exception(
                "No WalkingPad found. Make sure it is powered on, in range and not connected to another app (phone app, QWalkingPad).");
            return await TryConnect(found) ?? throw new Exception($"Found WalkingPad at {FormatAddress(found)} but could not connect.");
        }

        static ulong? ReadCachedAddress()
        {
            try { return File.Exists(CacheFile) ? ParseAddress(File.ReadAllText(CacheFile).Trim()) : null; }
            catch { return null; }
        }

        static ulong? ReadQWalkingPadAddress()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Dorian Rudolph\QWalkingPad");
                return k?.GetValue("LastUUID") is string s && s.Length > 0 ? ParseAddress(s) : null;
            }
            catch { return null; }
        }

        static async Task<ulong?> Scan(TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<ulong?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (_, e) =>
            {
                var name = e.Advertisement.LocalName ?? "";
                Log($"Seen {FormatAddress(e.BluetoothAddress)} '{name}'");
                if (name.Contains("walkingpad", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("kingsmith", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("KS-", StringComparison.OrdinalIgnoreCase)
                    || e.Advertisement.ServiceUuids.Contains(ServiceUuid))
                {
                    Status($"Found '{name}' at {FormatAddress(e.BluetoothAddress)}");
                    tcs.TrySetResult(e.BluetoothAddress);
                }
            };
            watcher.Start();
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            watcher.Stop();
            return done == tcs.Task ? tcs.Task.Result : null;
        }

        static async Task<Pad?> TryConnect(ulong addr)
        {
            var pad = new Pad();
            try
            {
                pad.device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                if (pad.device == null) { Log("Device not found"); return null; }
                Log($"Device '{pad.device.Name}'");

                GattDeviceServicesResult sr = null!;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    sr = await pad.device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
                    if (sr.Status == GattCommunicationStatus.Success && sr.Services.Count > 0) break;
                    Log($"Service discovery: {sr.Status}, retrying");
                    await Task.Delay(1000);
                }
                if (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0) { Log("FE00 service not available"); pad.Dispose(); return null; }
                pad.service = sr.Services[0];

                var access = await pad.service.RequestAccessAsync();
                Log($"Access: {access}");
                var open = await pad.service.OpenAsync(GattSharingMode.SharedReadAndWrite);
                Log($"Open: {open}");

                var cr = await pad.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (cr.Status != GattCommunicationStatus.Success) { Log($"Characteristics: {cr.Status}"); pad.Dispose(); return null; }
                pad.notifyChar = cr.Characteristics.FirstOrDefault(c => c.Uuid == NotifyUuid)!;
                pad.writeChar = cr.Characteristics.FirstOrDefault(c => c.Uuid == WriteUuid)!;
                if (pad.notifyChar == null || pad.writeChar == null) { Log("FE01/FE02 not found"); pad.Dispose(); return null; }
                Log($"FE01 props: {pad.notifyChar.CharacteristicProperties}, FE02 props: {pad.writeChar.CharacteristicProperties}");
                pad.writeOption = pad.writeChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
                    ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;

                pad.notifyChar.ValueChanged += pad.OnNotify;
                var st = await pad.notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (st != GattCommunicationStatus.Success) { Log($"Subscribe: {st}"); pad.Dispose(); return null; }

                Status($"Connected to '{pad.device.Name}' ({FormatAddress(addr)})");
                try { Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!); File.WriteAllText(CacheFile, FormatAddress(addr)); } catch { }
                return pad;
            }
            catch (Exception e)
            {
                Log($"Connect failed: {e.Message}");
                pad.Dispose();
                return null;
            }
        }

        void OnNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var m = args.CharacteristicValue.ToArray();
            Log($"<- {Convert.ToHexString(m)}");
            if (m.Length < 2) return;
            static uint Int(byte[] b, int i) => (uint)(b[i] << 16 | b[i + 1] << 8 | b[i + 2]);
            lock (gate)
            {
                if (m[1] == 0xa2 && m.Length >= 15)
                {
                    lastInfo = new Info(m[2], m[3], m[4], Int(m, 5), Int(m, 8), Int(m, 11));
                    infoSignal.TrySetResult();
                }
                else if (m[1] == 0xa6 && m.Length >= 14)
                {
                    lastParams = new Params(m[2], Int(m, 3), m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13]);
                    paramsSignal.TrySetResult();
                }
            }
        }

        public async Task Send(byte[] data)
        {
            var wait = lastSend + CommandGap - DateTime.Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            Log($"-> {Convert.ToHexString(data)}");
            var r = await writeChar.WriteValueWithResultAsync(data.AsBuffer(), writeOption);
            lastSend = DateTime.Now;
            if (r.Status != GattCommunicationStatus.Success)
                throw new Exception($"Write failed: {r.Status}");
        }

        public async Task<Info?> Query(TimeSpan? timeout = null)
        {
            Task signal;
            lock (gate) { infoSignal = new(TaskCreationOptions.RunContinuationsAsynchronously); signal = infoSignal.Task; }
            await Send(Cmd(0, 0));
            await Task.WhenAny(signal, Task.Delay(timeout ?? TimeSpan.FromSeconds(2)));
            lock (gate) return signal.IsCompleted ? lastInfo : null;
        }

        // the first notification after (re)connecting can take a while; resend the query a few times
        public async Task<Info?> InitialQuery()
        {
            for (int i = 0; i < 8; i++)
            {
                var info = await Query(TimeSpan.FromSeconds(0.8));
                if (info != null) return info;
                Log("No state notification yet, retrying");
            }
            return null;
        }

        public async Task<Params?> QueryParams()
        {
            Task signal;
            lock (gate) { paramsSignal = new(TaskCreationOptions.RunContinuationsAsynchronously); signal = paramsSignal.Task; }
            await Send(CmdInt(0, 0));
            await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(2)));
            lock (gate) return signal.IsCompleted ? lastParams : null;
        }

        public async Task<Info?> WaitFor(Func<Info, bool> cond, TimeSpan timeout)
        {
            var end = DateTime.Now + timeout;
            while (DateTime.Now < end)
            {
                var i = await Query();
                if (i != null) { Log($"   {i}"); if (cond(i)) return i; }
            }
            return null;
        }

        public void Dispose()
        {
            try { if (notifyChar != null) notifyChar.ValueChanged -= OnNotify; } catch { }
            service?.Dispose();
            device?.Dispose();
        }
    }
}
