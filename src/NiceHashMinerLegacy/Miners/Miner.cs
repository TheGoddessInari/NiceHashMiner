﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NiceHashMiner.Configs;
using NiceHashMiner.Interfaces;
using NiceHashMiner.Miners;
using NiceHashMiner.Miners.Grouping;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using NiceHashMiner.Algorithms;
using NiceHashMiner.Devices;
using NiceHashMinerLegacy.Common.Enums;
using Timer = System.Timers.Timer;
using System.ComponentModel;

namespace NiceHashMiner
{
    // 
    public class MinerPidData
    {
        public string MinerBinPath;
        public int Pid = -1;
    }

    public abstract class Miner
    {
        // MinerIDCount used to identify miners creation
        protected static long MinerIDCount { get; private set; }


        public NhmConectionType ConectionType { get; protected set; }

        // used to identify miner instance
        protected readonly long MinerID;

        private string _minerTag;
        public string MinerDeviceName { get; set; }

        protected int ApiPort { get; private set; }

        // if miner has no API bind port for reading curentlly only CryptoNight on ccminer
        public bool IsApiReadException { get; protected set; }

        public bool IsNeverHideMiningWindow { get; protected set; }

        // mining algorithm stuff
        protected bool IsInit { get; private set; }

        public MiningSetup MiningSetup { get; protected set; }

        // sgminer/zcash claymore workaround
        protected bool IsKillAllUsedMinerProcs { get; set; }

        public bool IsRunning { get; protected set; }
        protected string Path { get; private set; }

        protected string LastCommandLine { get; set; }

        // the defaults will be 
        protected string WorkingDirectory { get; private set; }

        protected string MinerExeName { get; private set; }
        protected NiceHashProcess ProcessHandle;
        protected MinerPidData _currentPidData;
        protected readonly List<MinerPidData> _allPidData = new List<MinerPidData>();

        // Benchmark stuff
        public bool BenchmarkSignalQuit;

        public bool BenchmarkSignalHanged;
        private Stopwatch _benchmarkTimeOutStopWatch;
        public bool BenchmarkSignalTimedout;
        protected bool BenchmarkSignalFinnished;
        protected IBenchmarkComunicator BenchmarkComunicator;
        protected bool OnBenchmarkCompleteCalled;
        protected Algorithm BenchmarkAlgorithm { get; set; }
        public BenchmarkProcessStatus BenchmarkProcessStatus { get; protected set; }
        protected string BenchmarkProcessPath { get; set; }
        protected Process BenchmarkHandle { get; set; }
        protected Exception BenchmarkException;
        protected int BenchmarkTimeInSeconds;

        private string _benchmarkLogPath = "";
        protected List<string> BenchLines;

        protected bool TimeoutStandard;


        // TODO maybe set for individual miner cooldown/retries logic variables
        // this replaces MinerAPIGraceSeconds(AMD)
        private const int MinCooldownTimeInMilliseconds = 5 * 1000; // 5 seconds
        //private const int _MIN_CooldownTimeInMilliseconds = 1000; // TESTING

        //private const int _MAX_CooldownTimeInMilliseconds = 60 * 1000; // 1 minute max, whole waiting time 75seconds
        private readonly int _maxCooldownTimeInMilliseconds; // = GetMaxCooldownTimeInMilliseconds();

        protected abstract int GetMaxCooldownTimeInMilliseconds();
        private Timer _cooldownCheckTimer;
        protected MinerApiReadStatus CurrentMinerReadStatus { get; set; }
        private int _currentCooldownTimeInSeconds = MinCooldownTimeInMilliseconds;
        private int _currentCooldownTimeInSecondsLeft = MinCooldownTimeInMilliseconds;
        private const int IsCooldownCheckTimerAliveCap = 15;
        private bool _needsRestart;

        public bool _isEnded { get; private set; }

        public bool IsUpdatingApi = false;

        protected const string HttpHeaderDelimiter = "\r\n\r\n";

        protected bool IsMultiType;

//// PRODUCTION
//#if !(TESTNET || TESTNETDEV)
        protected Dictionary<string, string> _enviormentVariables = null;
//#endif
//// TESTNET
//#if TESTNET || TESTNETDEV
        protected IEnumerable<ComputeDevice> Devices => MiningSetup.MiningPairs.Select(p => p.Device);
//#endif


        protected Miner(string minerDeviceName)
        {
            ConectionType = NhmConectionType.STRATUM_TCP;
            MiningSetup = new MiningSetup(null);
            IsInit = false;
            MinerID = MinerIDCount++;

            MinerDeviceName = minerDeviceName;

            WorkingDirectory = "";

            IsRunning = false;

            LastCommandLine = "";

            IsApiReadException = false;
            // Only set minimize if hide is false (specific miners will override true after)
            IsNeverHideMiningWindow = ConfigManager.GeneralConfig.MinimizeMiningWindows &&
                                      !ConfigManager.GeneralConfig.HideMiningWindows;
            IsKillAllUsedMinerProcs = false;
            _maxCooldownTimeInMilliseconds = GetMaxCooldownTimeInMilliseconds();
            // 
            Helpers.ConsolePrint(MinerTag(), "NEW MINER CREATED");
        }

        ~Miner()
        {
            // free the port
            MinersApiPortsManager.RemovePort(ApiPort);
            Helpers.ConsolePrint(MinerTag(), "MINER DESTROYED");
        }

        protected void SetWorkingDirAndProgName(string fullPath)
        {
            WorkingDirectory = "";
            Path = fullPath;
            var lastIndex = fullPath.LastIndexOf("\\") + 1;
            if (lastIndex > 0)
            {
                WorkingDirectory = fullPath.Substring(0, lastIndex);
                MinerExeName = fullPath.Substring(lastIndex);
            }
        }

        private void SetApiPort()
        {
            if (IsInit)
            {
                var minerBase = MiningSetup.MiningPairs[0].Algorithm.MinerBaseType;
                var algoType = MiningSetup.MiningPairs[0].Algorithm.NiceHashID;
                var path = MiningSetup.MinerPath;

                ApiPort = -1; // not set
                ApiPort = MinersApiPortsManager.GetAvaliablePort();
            }
        }

        public virtual void InitMiningSetup(MiningSetup miningSetup)
        {
            MiningSetup = miningSetup;
            IsInit = MiningSetup.IsInit;
            SetApiPort();
            SetWorkingDirAndProgName(MiningSetup.MinerPath);
        }

        public void InitBenchmarkSetup(MiningPair benchmarkPair)
        {
            InitMiningSetup(new MiningSetup(new List<MiningPair>()
            {
                benchmarkPair
            }));
            BenchmarkAlgorithm = benchmarkPair.Algorithm;
            BenchmarkAlgorithm.AvaragedSpeed = 0; // reset this to zero because we might have it from prev mining session
        }

        // TAG for identifying miner
        public string MinerTag()
        {
            if (_minerTag == null)
            {
                const string mask = "{0}-MINER_ID({1})-DEVICE_IDs({2})";
                // no devices set
                if (!IsInit)
                {
                    return string.Format(mask, MinerDeviceName, MinerID, "NOT_SET");
                }

                // contains ids
                var ids = MiningSetup.MiningPairs.Select(cdevs => cdevs.Device.ID.ToString()).ToList();
                _minerTag = string.Format(mask, MinerDeviceName, MinerID, string.Join(",", ids));
            }

            return _minerTag;
        }

        private static string ProcessTag(MinerPidData pidData)
        {
            return $"[pid({pidData.Pid})|bin({pidData.MinerBinPath})]";
        }

        public string ProcessTag()
        {
            return _currentPidData == null ? "PidData is NULL" : ProcessTag(_currentPidData);
        }

        public void KillAllUsedMinerProcesses()
        {
            var toRemovePidData = new List<MinerPidData>();
            Helpers.ConsolePrint(MinerTag(), "Trying to kill all miner processes for this instance:");
            foreach (var pidData in _allPidData)
            {
                try
                {
                    var process = Process.GetProcessById(pidData.Pid);
                    if (pidData.MinerBinPath.Contains(process.ProcessName))
                    {
                        Helpers.ConsolePrint(MinerTag(), $"Trying to kill {ProcessTag(pidData)}");
                        try
                        {
                            process.Kill();
                            process.Close();
                            process.WaitForExit(1000 * 60 * 1);
                        }
                        catch (Exception e)
                        {
                            Helpers.ConsolePrint(MinerTag(),
                                $"Exception killing {ProcessTag(pidData)}, exMsg {e.Message}");
                        }
                    }
                }
                catch (Exception e)
                {
                    toRemovePidData.Add(pidData);
                    Helpers.ConsolePrint(MinerTag(), $"Nothing to kill {ProcessTag(pidData)}, exMsg {e.Message}");
                }
            }

            _allPidData.RemoveAll(x => toRemovePidData.Contains(x));
        }

// PRODUCTION
#if !(TESTNET || TESTNETDEV)
        public abstract void Start(string url, string btcAdress, string worker);

        protected string GetUsername(string btcAdress, string worker)
        {
            if (worker.Length > 0)
            {
                return btcAdress + "." + worker;
            }

            return btcAdress;
        }
#endif
// TESTNET
#if TESTNET || TESTNETDEV
        public abstract void Start(string url, string username);
#endif


        protected abstract void _Stop(MinerStopType willswitch);

        public virtual void Stop(MinerStopType willswitch = MinerStopType.SWITCH)
        {
            _cooldownCheckTimer?.Stop();
            _Stop(willswitch);
            IsRunning = false;
        }

        public void End()
        {
            _isEnded = true;
            Stop(MinerStopType.FORCE_END);
        }

        protected void ShutdownMiner(bool withCrtlC = false)
        {
            if (IsRunning)
            {
                Helpers.ConsolePrint(MinerTag(), ProcessTag() + " Shutting down miner");
            }

            if (ProcessHandle != null)
            {
                try
                {
                    if (withCrtlC)
                    {
                        ProcessHandle.SendCtrlC((uint) Process.GetCurrentProcess().Id);
                    }
                    else
                    {
                        ProcessHandle.Kill();
                    }
                }
                catch { }

                ProcessHandle.Close();
                ProcessHandle = null;

                // sgminer needs to be removed and kill by PID
                if (IsKillAllUsedMinerProcs) KillAllUsedMinerProcesses();
            }
        }

        protected void KillProspectorClaymoreMinerBase(string exeName)
        {
            foreach (var process in Process.GetProcessesByName(exeName))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception e)
                {
                    unchecked
                    {
                        // Suppress reporting of Access Denied error
                        if (!(e is Win32Exception w && w.HResult != (int) 0x80004005))
                        {
                            Helpers.ConsolePrint(MinerDeviceName, e.ToString());
                        }
                    }
                }
            }
        }

        protected virtual string GetDevicesCommandString()
        {
            var deviceStringCommand = " ";

            var ids = MiningSetup.MiningPairs.Select(mPair => mPair.Device.ID.ToString()).ToList();
            deviceStringCommand += string.Join(",", ids);

            return deviceStringCommand;
        }

        #region BENCHMARK DE-COUPLED Decoupled benchmarking routines

        public int BenchmarkTimeoutInSeconds(int timeInSeconds)
        {
            if (TimeoutStandard) return timeInSeconds;
            if (BenchmarkAlgorithm.NiceHashID == AlgorithmType.DaggerHashimoto)
            {
                return 5 * 60 + 120; // 5 minutes plus two minutes
            }

            //if (BenchmarkAlgorithm.NiceHashID == AlgorithmType.CryptoNight)
            //{
            //    return 5 * 60 + 120; // 5 minutes plus two minutes
            //}

            return timeInSeconds + 120; // wait time plus two minutes
        }

        // TODO remove algorithm
        protected abstract string BenchmarkCreateCommandLine(Algorithm algorithm, int time);

        public class BenchmarkResult
        {
            public bool Success { get; set; }
            public string Status { get; set; }
        }

        // todo 
        public class BenchmarkResultTask : TaskCompletionSource<BenchmarkResult>, IBenchmarkComunicator
        {
            public void OnBenchmarkComplete(bool success, string status)
            {
                this.SetResult(new BenchmarkResult
                {
                    Status = status,
                    Success = success
                });
            }
        }

        public Task<BenchmarkResult> BenchmarkStartAsync(int time, CancellationToken stop)
        {
            var tcs = new BenchmarkResultTask();
            BenchmarkStart(time, tcs);
            stop.Register(() => {
                BenchmarkSignalQuit = true;
                InvokeBenchmarkSignalQuit();
            });
            return tcs.Task;
        }

        // The benchmark config and algorithm must guarantee that they are compatible with miner
        // we guarantee algorithm is supported
        // we will not have empty benchmark configs, all benchmark configs will have device list
        public virtual void BenchmarkStart(int time, IBenchmarkComunicator benchmarkComunicator)
        {
            BenchmarkComunicator = benchmarkComunicator;
            BenchmarkTimeInSeconds = time;
            BenchmarkSignalFinnished = true;
            // check and kill 
            BenchmarkHandle = null;
            OnBenchmarkCompleteCalled = false;
            _benchmarkTimeOutStopWatch = null;


            try
            {
                if (!Directory.Exists("logs"))
                {
                    Directory.CreateDirectory("logs");
                }
            }
            catch { }

            BenchLines = new List<string>();
            _benchmarkLogPath =
                $"{Logger.LogPath}Bench_{MiningSetup.MiningPairs[0].Device.Uuid}_{MiningSetup.MiningPairs[0].Algorithm.AlgorithmStringID}.log";

            var commandLine = BenchmarkCreateCommandLine(BenchmarkAlgorithm, time);

            var benchmarkThread = new Thread(BenchmarkThreadRoutine);
            benchmarkThread.Start(commandLine);
        }

        protected virtual Process BenchmarkStartProcess(string commandLine)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Helpers.ConsolePrint(MinerTag(), "Starting benchmark: " + commandLine);

            var benchmarkHandle = new Process
            {
                StartInfo =
                {
                    FileName = MiningSetup.MinerPath
                }
            };


            //// sgminer quickfix
            //if (this is Sgminer)
            //{
            //    BenchmarkProcessPath = "cmd / " + benchmarkHandle.StartInfo.FileName;
            //    benchmarkHandle.StartInfo.FileName = "cmd";
            //}
            //else
            //{
                BenchmarkProcessPath = benchmarkHandle.StartInfo.FileName;
                Helpers.ConsolePrint(MinerTag(), "Using miner: " + benchmarkHandle.StartInfo.FileName);
                benchmarkHandle.StartInfo.WorkingDirectory = WorkingDirectory;
            //}

            benchmarkHandle.StartInfo.Arguments = commandLine;
            benchmarkHandle.StartInfo.UseShellExecute = false;
            benchmarkHandle.StartInfo.RedirectStandardError = true;
            benchmarkHandle.StartInfo.RedirectStandardOutput = true;
            benchmarkHandle.StartInfo.CreateNoWindow = true;
            benchmarkHandle.OutputDataReceived += BenchmarkOutputErrorDataReceived;
            benchmarkHandle.ErrorDataReceived += BenchmarkOutputErrorDataReceived;
            benchmarkHandle.Exited += BenchmarkHandle_Exited;

            if (_enviormentVariables != null)
            {
                foreach (var kvp in _enviormentVariables)
                {
                    benchmarkHandle.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
                }
            }

            EthlargementOld.CheckAndStart(MiningSetup);

            if (!benchmarkHandle.Start()) return null;

            _currentPidData = new MinerPidData
            {
                MinerBinPath = benchmarkHandle.StartInfo.FileName,
                Pid = benchmarkHandle.Id
            };
            _allPidData.Add(_currentPidData);

            return benchmarkHandle;
        }

        private void BenchmarkHandle_Exited(object sender, EventArgs e)
        {
            BenchmarkSignalFinnished = true;
        }

        private void BenchmarkOutputErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (_benchmarkTimeOutStopWatch == null)
            {
                _benchmarkTimeOutStopWatch = new Stopwatch();
                _benchmarkTimeOutStopWatch.Start();
            }
            else if (_benchmarkTimeOutStopWatch.Elapsed.TotalSeconds >
                     BenchmarkTimeoutInSeconds(BenchmarkTimeInSeconds))
            {
                _benchmarkTimeOutStopWatch.Stop();
                if (BenchmarkAlgorithm.AvaragedSpeed > 0)
                {
                    // fallback to this one
                    BenchmarkAlgorithm.BenchmarkSpeed = BenchmarkAlgorithm.AvaragedSpeed;
                    BenchmarkSignalFinnished = true;
                }
                else
                {
                    BenchmarkSignalTimedout = true;
                }
            }

            var outdata = e.Data;
            if (e.Data != null)
            {
                BenchmarkOutputErrorDataReceivedImpl(outdata);
            }

            // terminate process situations
            if (BenchmarkSignalQuit
                || BenchmarkSignalFinnished
                || BenchmarkSignalHanged
                || BenchmarkSignalTimedout
                || BenchmarkException != null)
            {
                FinishUpBenchmark();
                EndBenchmarkProcces();
            }
        }

        protected virtual void FinishUpBenchmark()
        { }

        protected abstract void BenchmarkOutputErrorDataReceivedImpl(string outdata);

        protected void CheckOutdata(string outdata)
        {
            //Helpers.ConsolePrint("BENCHMARK" + benchmarkLogPath, outdata);
            BenchLines.Add(outdata);
            // ccminer, cpuminer
            if (outdata.Contains("Cuda error"))
                BenchmarkException = new Exception("CUDA error");
            if (outdata.Contains("is not supported"))
                BenchmarkException = new Exception("N/A");
            if (outdata.Contains("illegal memory access"))
                BenchmarkException = new Exception("CUDA error");
            if (outdata.Contains("unknown error"))
                BenchmarkException = new Exception("Unknown error");
            if (outdata.Contains("No servers could be used! Exiting."))
                BenchmarkException = new Exception("No pools or work can be used for benchmarking");
            if (outdata.Contains("Error CL_INVALID_KERNEL"))
                BenchmarkException = new Exception("Error CL_INVALID_KERNEL");
            if (outdata.Contains("Error CL_INVALID_KERNEL_ARGS"))
                BenchmarkException = new Exception("Error CL_INVALID_KERNEL_ARGS");
            //if (outdata.Contains("error") || outdata.Contains("Error"))
            //    BenchmarkException = new Exception("Unknown error #2");
            // Ethminer
            if (outdata.Contains("No GPU device with sufficient memory was found"))
                BenchmarkException = new Exception("[daggerhashimoto] No GPU device with sufficient memory was found.");
            // xmr-stak
            if (outdata.Contains("Press any key to exit"))
                BenchmarkException = new Exception("Xmr-Stak erred, check its logs");

            // lastly parse data
            if (BenchmarkParseLine(outdata))
            {
                BenchmarkSignalFinnished = true;
            }
        }

        public virtual void InvokeBenchmarkSignalQuit()
        {
            KillAllUsedMinerProcesses();
        }

        // killing proccesses can take time
        public virtual void EndBenchmarkProcces()
        {
            if (BenchmarkHandle != null && BenchmarkProcessStatus != BenchmarkProcessStatus.Killing &&
                BenchmarkProcessStatus != BenchmarkProcessStatus.DoneKilling)
            {
                BenchmarkProcessStatus = BenchmarkProcessStatus.Killing;
                try
                {
                    Helpers.ConsolePrint("BENCHMARK",
                        $"Trying to kill benchmark process {BenchmarkProcessPath} algorithm {BenchmarkAlgorithm.AlgorithmName}");
                    BenchmarkHandle.Kill();
                    BenchmarkHandle.Close();
                    KillAllUsedMinerProcesses();
                }
                catch { }
                finally
                {
                    BenchmarkProcessStatus = BenchmarkProcessStatus.DoneKilling;
                    Helpers.ConsolePrint("BENCHMARK",
                        $"Benchmark process {BenchmarkProcessPath} algorithm {BenchmarkAlgorithm.AlgorithmName} KILLED");
                    //BenchmarkHandle = null;
                }
            }
        }


        protected virtual void BenchmarkThreadRoutineStartSettup()
        {
            BenchmarkHandle.BeginErrorReadLine();
            BenchmarkHandle.BeginOutputReadLine();
        }

        protected void BenchmarkThreadRoutineCatch(Exception ex)
        {
            BenchmarkAlgorithm.BenchmarkSpeed = 0;

            Helpers.ConsolePrint(MinerTag(), "Benchmark Exception: " + ex.Message);
            if (BenchmarkComunicator != null && !OnBenchmarkCompleteCalled)
            {
                OnBenchmarkCompleteCalled = true;
                BenchmarkComunicator.OnBenchmarkComplete(false, GetFinalBenchmarkString());
            }
        }

        protected virtual string GetFinalBenchmarkString()
        {
            return BenchmarkSignalTimedout && !TimeoutStandard
                ? Translations.Tr("Timed out")
                : Translations.Tr("Terminated");
        }

        protected virtual void BenchmarkThreadRoutineFinish()
        {
            var status = BenchmarkProcessStatus.Finished;

            if (!BenchmarkAlgorithm.BenchmarkNeeded)
            {
                status = BenchmarkProcessStatus.Success;
            }

            try
            {
                using (StreamWriter sw = File.AppendText(_benchmarkLogPath))
                {
                    foreach (var line in BenchLines)
                    {
                        sw.WriteLine(line);
                    }
                }
            }
            catch { }

            BenchmarkProcessStatus = status;
            if (BenchmarkAlgorithm is DualAlgorithm dualAlg)
            {
                if (!dualAlg.TuningEnabled)
                {
                    // Tuning will report speed
                    Helpers.ConsolePrint("BENCHMARK",
                        "Final Speed: " + Helpers.FormatDualSpeedOutput(dualAlg.BenchmarkSpeed,
                            dualAlg.SecondaryBenchmarkSpeed, dualAlg.DualNiceHashID));
                }
            }
            else
            {
                Helpers.ConsolePrint("BENCHMARK",
                    "Final Speed: " + Helpers.FormatDualSpeedOutput(BenchmarkAlgorithm.BenchmarkSpeed, 0,
                        BenchmarkAlgorithm.NiceHashID));
            }

            Helpers.ConsolePrint("BENCHMARK", "Benchmark ends");
            if (BenchmarkComunicator != null && !OnBenchmarkCompleteCalled)
            {
                OnBenchmarkCompleteCalled = true;
                var isOK = BenchmarkProcessStatus.Success == status;
                var msg = GetFinalBenchmarkString();
                BenchmarkComunicator.OnBenchmarkComplete(isOK, isOK ? "" : msg);
            }
        }


        protected virtual void BenchmarkThreadRoutine(object commandLine)
        {
            BenchmarkSignalQuit = false;
            BenchmarkSignalHanged = false;
            BenchmarkSignalFinnished = false;
            BenchmarkException = null;
            
            Thread.Sleep(ConfigManager.GeneralConfig.MinerRestartDelayMS);

            try
            {
                Helpers.ConsolePrint("BENCHMARK", "Benchmark starts");
                BenchmarkHandle = BenchmarkStartProcess((string) commandLine);

                BenchmarkThreadRoutineStartSettup();
                // wait a little longer then the benchmark routine if exit false throw
                //var timeoutTime = BenchmarkTimeoutInSeconds(BenchmarkTimeInSeconds);
                //var exitSucces = BenchmarkHandle.WaitForExit(timeoutTime * 1000);
                // don't use wait for it breaks everything
                BenchmarkProcessStatus = BenchmarkProcessStatus.Running;
                var exited = BenchmarkHandle.WaitForExit((BenchmarkTimeoutInSeconds(BenchmarkTimeInSeconds) + 20) * 1000);
                if (BenchmarkSignalTimedout && !TimeoutStandard)
                {
                    throw new Exception("Benchmark timedout");
                }

                if (BenchmarkException != null)
                {
                    throw BenchmarkException;
                }

                if (BenchmarkSignalQuit)
                {
                    throw new Exception("Termined by user request");
                }

                if (BenchmarkSignalHanged || !exited)
                {
                    throw new Exception("Miner is not responding");
                }

                if (BenchmarkSignalFinnished)
                {
                    //break;
                }
            }
            catch (Exception ex)
            {
                BenchmarkThreadRoutineCatch(ex);
            }
            finally
            {
                BenchmarkThreadRoutineFinish();
            }
        }

        /// <summary>
        /// Thread routine for miners that cannot be scheduled to stop and need speed data read from command line
        /// </summary>
        /// <param name="commandLine"></param>
        /// <param name="benchmarkTimeWait"></param>
        protected void BenchmarkThreadRoutineAlternate(object commandLine, int benchmarkTimeWait)
        {
            CleanOldLogs();

            BenchmarkSignalQuit = false;
            BenchmarkSignalHanged = false;
            BenchmarkSignalFinnished = false;
            BenchmarkException = null;
            
            Thread.Sleep(ConfigManager.GeneralConfig.MinerRestartDelayMS);

            try
            {
                Helpers.ConsolePrint("BENCHMARK", "Benchmark starts");
                Helpers.ConsolePrint(MinerTag(), "Benchmark should end in : " + benchmarkTimeWait + " seconds");
                BenchmarkHandle = BenchmarkStartProcess((string) commandLine);
                BenchmarkHandle.WaitForExit(benchmarkTimeWait + 2);
                var benchmarkTimer = new Stopwatch();
                benchmarkTimer.Reset();
                benchmarkTimer.Start();
                //BenchmarkThreadRoutineStartSettup();
                // wait a little longer then the benchmark routine if exit false throw
                //var timeoutTime = BenchmarkTimeoutInSeconds(BenchmarkTimeInSeconds);
                //var exitSucces = BenchmarkHandle.WaitForExit(timeoutTime * 1000);
                // don't use wait for it breaks everything
                BenchmarkProcessStatus = BenchmarkProcessStatus.Running;
                var keepRunning = true;
                while (keepRunning && IsActiveProcess(BenchmarkHandle.Id))
                {
                    //string outdata = BenchmarkHandle.StandardOutput.ReadLine();
                    //BenchmarkOutputErrorDataReceivedImpl(outdata);
                    // terminate process situations
                    if (benchmarkTimer.Elapsed.TotalSeconds >= (benchmarkTimeWait + 2)
                        || BenchmarkSignalQuit
                        || BenchmarkSignalFinnished
                        || BenchmarkSignalHanged
                        || BenchmarkSignalTimedout
                        || BenchmarkException != null)
                    {
                        var imageName = MinerExeName.Replace(".exe", "");
                        // maybe will have to KILL process
                        KillProspectorClaymoreMinerBase(imageName);
                        if (BenchmarkSignalTimedout)
                        {
                            throw new Exception("Benchmark timedout");
                        }

                        if (BenchmarkException != null)
                        {
                            throw BenchmarkException;
                        }

                        if (BenchmarkSignalQuit)
                        {
                            throw new Exception("Termined by user request");
                        }

                        if (BenchmarkSignalFinnished)
                        {
                            break;
                        }

                        keepRunning = false;
                        break;
                    }

                    // wait a second reduce CPU load
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                BenchmarkThreadRoutineCatch(ex);
            }
            finally
            {
                BenchmarkAlgorithm.BenchmarkSpeed = 0;
                // find latest log file
                string latestLogFile = "";
                var dirInfo = new DirectoryInfo(WorkingDirectory);
                foreach (var file in dirInfo.GetFiles(GetLogFileName()))
                {
                    latestLogFile = file.Name;
                    break;
                }

                BenchmarkHandle?.WaitForExit(10000);
                // read file log
                if (File.Exists(WorkingDirectory + latestLogFile))
                {
                    var lines = File.ReadAllLines(WorkingDirectory + latestLogFile);
                    ProcessBenchLinesAlternate(lines);
                }

                BenchmarkThreadRoutineFinish();
            }
        }

        protected void CleanOldLogs()
        {
            // clean old logs
            try
            {
                var dirInfo = new DirectoryInfo(WorkingDirectory);
                var deleteContains = GetLogFileName();
                if (dirInfo.Exists)
                {
                    foreach (var file in dirInfo.GetFiles())
                    {
                        if (file.Name.Contains(deleteContains))
                        {
                            file.Delete();
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// When parallel benchmarking each device needs its own log files, so this uniquely identifies for the setup
        /// </summary>
        protected string GetDeviceID()
        {
            var ids = MiningSetup.MiningPairs.Select(x => x.Device.ID);
            var idStr = string.Join(",", ids);

            if (!IsMultiType) return idStr;
            
            // Miners that use multiple dev types need to also discriminate based on that
            var types = MiningSetup.MiningPairs.Select(x => (int) x.Device.DeviceType);
            return $"{string.Join(",", types)}-{idStr}";
        }

        protected string GetLogFileName()
        {
            return $"{GetDeviceID()}_log.txt";
        }

        protected virtual void ProcessBenchLinesAlternate(string[] lines)
        { }

        protected abstract bool BenchmarkParseLine(string outdata);

        protected string GetServiceUrl(AlgorithmType algo)
        {
// PRODUCTION
#if !(TESTNET || TESTNETDEV)
            return Globals.GetLocationUrl(algo, Globals.MiningLocation[ConfigManager.GeneralConfig.ServiceLocation],
                ConectionType);
#endif
// TESTNET
#if TESTNET || TESTNETDEV
            return ApplicationStateManager.GetSelectedServiceLocationLocationUrl(algo, ConectionType);
#endif
        }

        protected bool IsActiveProcess(int pid)
        {
            try
            {
                return Process.GetProcessById(pid) != null;
            }
            catch
            {
                return false;
            }
        }

        #endregion //BENCHMARK DE-COUPLED Decoupled benchmarking routines
        

        protected virtual NiceHashProcess _Start()
        {
            // never start when ended
            if (_isEnded)
            {
                return null;
            }

            if (LastCommandLine.Length == 0) return null;

            EthlargementOld.CheckAndStart(MiningSetup);

            var P = new NiceHashProcess();

            if (WorkingDirectory.Length > 1)
            {
                P.StartInfo.WorkingDirectory = WorkingDirectory;
            }

            if (_enviormentVariables != null)
            {
                foreach (var kvp in _enviormentVariables)
                {
                    P.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
                }
            }

            P.StartInfo.FileName = Path;
            P.ExitEvent = Miner_Exited;

            P.StartInfo.Arguments = LastCommandLine;
            if (IsNeverHideMiningWindow)
            {
                P.StartInfo.CreateNoWindow = false;
                if (ConfigManager.GeneralConfig.HideMiningWindows || ConfigManager.GeneralConfig.MinimizeMiningWindows)
                {
                    P.StartInfo.WindowStyle = ProcessWindowStyle.Minimized;
                    P.StartInfo.UseShellExecute = true;
                }
            }
            else
            {
                P.StartInfo.CreateNoWindow = ConfigManager.GeneralConfig.HideMiningWindows;
            }

            P.StartInfo.UseShellExecute = false;

            try
            {
                if (P.Start())
                {
                    IsRunning = true;

                    _currentPidData = new MinerPidData
                    {
                        MinerBinPath = P.StartInfo.FileName,
                        Pid = P.Id
                    };
                    _allPidData.Add(_currentPidData);

                    Helpers.ConsolePrint(MinerTag(), "Starting miner " + ProcessTag() + " " + LastCommandLine);

                    StartCoolDownTimerChecker();

                    return P;
                }

                Helpers.ConsolePrint(MinerTag(), "NOT STARTED " + ProcessTag() + " " + LastCommandLine);
                return null;
            }
            catch (Exception ex)
            {
                Helpers.ConsolePrint(MinerTag(), ProcessTag() + " _Start: " + ex.Message);
                return null;
            }
        }

        protected void StartCoolDownTimerChecker()
        {
            if (ConfigManager.GeneralConfig.CoolDownCheckEnabled)
            {
                Helpers.ConsolePrint(MinerTag(), ProcessTag() + " Starting cooldown checker");
                if (_cooldownCheckTimer != null && _cooldownCheckTimer.Enabled) _cooldownCheckTimer.Stop();
                // cool down init
                _cooldownCheckTimer = new Timer()
                {
                    Interval = MinCooldownTimeInMilliseconds
                };
                _cooldownCheckTimer.Elapsed += MinerCoolingCheck_Tick;
                _cooldownCheckTimer.Start();
                _currentCooldownTimeInSeconds = MinCooldownTimeInMilliseconds;
                _currentCooldownTimeInSecondsLeft = _currentCooldownTimeInSeconds;
            }
            else
            {
                Helpers.ConsolePrint(MinerTag(), "Cooldown checker disabled");
            }

            CurrentMinerReadStatus = MinerApiReadStatus.NONE;
        }


        protected virtual void Miner_Exited()
        {
            ScheduleRestart(5000);
        }

        protected void ScheduleRestart(int ms)
        {
            var restartInMs = ConfigManager.GeneralConfig.MinerRestartDelayMS > ms
                ? ConfigManager.GeneralConfig.MinerRestartDelayMS
                : ms;
            Helpers.ConsolePrint(MinerTag(), ProcessTag() + $" Miner_Exited Will restart in {restartInMs} ms");
            if (ConfigManager.GeneralConfig.CoolDownCheckEnabled)
            {
                CurrentMinerReadStatus = MinerApiReadStatus.RESTART;
                _needsRestart = true;
                _currentCooldownTimeInSecondsLeft = restartInMs;
            }
            else
            {
                // directly restart since cooldown checker not running
                Thread.Sleep(restartInMs);
                Restart();
            }
        }

        protected void Restart()
        {
            if (_isEnded) return;
            Helpers.ConsolePrint(MinerTag(), ProcessTag() + " Restarting miner..");
            Stop(MinerStopType.END); // stop miner first
            Thread.Sleep(ConfigManager.GeneralConfig.MinerRestartDelayMS);
            ProcessHandle = _Start(); // start with old command line
        }

        public abstract Task<ApiData> GetSummaryAsync();

        #region Cooldown/retry logic

        /// <summary>
        /// decrement time for half current half time, if less then min ammend
        /// </summary>
        private void CoolDown()
        {
            if (_currentCooldownTimeInSeconds > MinCooldownTimeInMilliseconds)
            {
                _currentCooldownTimeInSeconds = MinCooldownTimeInMilliseconds;
                Helpers.ConsolePrint(MinerTag(),
                    $"{ProcessTag()} Reseting cool time = {MinCooldownTimeInMilliseconds} ms");
                CurrentMinerReadStatus = MinerApiReadStatus.NONE;
            }
        }

        /// <summary>
        /// increment time for half current half time, if more then max set restart
        /// </summary>
        private void CoolUp()
        {
            _currentCooldownTimeInSeconds *= 2;
            Helpers.ConsolePrint(MinerTag(),
                $"{ProcessTag()} Cooling UP, cool time is {_currentCooldownTimeInSeconds} ms");
            if (_currentCooldownTimeInSeconds > _maxCooldownTimeInMilliseconds)
            {
                CurrentMinerReadStatus = MinerApiReadStatus.RESTART;
                Helpers.ConsolePrint(MinerTag(), ProcessTag() + " MAX cool time exceeded. RESTARTING");
                Restart();
            }
        }

        private void MinerCoolingCheck_Tick(object sender, ElapsedEventArgs e)
        {
            if (_isEnded)
            {
                End();
                return;
            }

            _currentCooldownTimeInSecondsLeft -= (int) _cooldownCheckTimer.Interval;
            // if times up
            if (_currentCooldownTimeInSecondsLeft > 0) return;
            if (_needsRestart)
            {
                _needsRestart = false;
                Restart();
            }
            else
                switch (CurrentMinerReadStatus)
                {
                    case MinerApiReadStatus.GOT_READ:
                        CoolDown();
                        break;
                    case MinerApiReadStatus.READ_SPEED_ZERO:
                        Helpers.ConsolePrint(MinerTag(), ProcessTag() + " READ SPEED ZERO, will cool up");
                        CoolUp();
                        break;
                    case MinerApiReadStatus.RESTART:
                        Restart();
                        break;
                    default:
                        CoolUp();
                        break;
                }

            // set new times left from the CoolUp/Down change
            _currentCooldownTimeInSecondsLeft = _currentCooldownTimeInSeconds;
        }

        #endregion //Cooldown/retry logic
    }
}
