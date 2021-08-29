using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

public static class ErrLogger
{

    #region Enums
    public enum Severity
    {
        Info,
        Warning,
        Error,
        Exception,
        Fatal
    }
    #endregion

    #region Fields
    private static string defaultFileName;
    private static string _prefix;
    private static readonly object _logFileSyncRoot = new object();
    private static readonly Queue<LogEntry> _logEntryQueue = new Queue<LogEntry>();
    private static Task _backgroundTask;
    private static readonly object _backgroundTaskSyncRoot = new object();
    private static DirectoryInfo _logDirTmp = new DirectoryInfo(Directory.GetCurrentDirectory());
    private static DirectoryInfo _logDirApp = new DirectoryInfo(Directory.GetCurrentDirectory());
    private static bool _tmpLogStopped;
    #endregion

    #region Properties
    public static bool TmpLogStopped
    {
        get
        {
            return _tmpLogStopped;
        }
        set
        {
            _tmpLogStopped = value;
        }
    }
    public static string Prefix
    {
        get
        {
            return _prefix ?? string.Empty;
        }
        set
        {
            _prefix = value;
        }
    }
    public static string DirApp
    {
        get
        {
            return _logDirApp.FullName;
        }
    }
    public static string DirTmp
    {
        get
        {
            return _logDirTmp.FullName;
        }
    }
    public static bool ErrorsFound { get; set; }
    public static bool StopLoggingRequested
    {
        get;
        private set;
    }
    public static int NumberOfLogEntriesWaitingToBeWrittenToFile
    {
        get
        {
            return _logEntryQueue.Count;
        }
    }
    public static bool StopEnqueingNewEntries
    {
        get;
        private set;
    }
    public static bool StartExplicitly
    {
        get;
        set;
    }
    public static bool LoggingStarted
    {
        get
        {
            return _backgroundTask != null;
        }
    }
    #endregion

    static ErrLogger()
    {
        defaultFileName = "logs.err";
        AppDomain.CurrentDomain.ProcessExit += CurrentDomainProcessExit;
    }

    static void CurrentDomainProcessExit(object sender, EventArgs e)
    {
        if (!TmpLogStopped)
        {
            StopTmpLog();
        }
        Stop();
    }

    #region Public Methods
    public static Exception Configure(string logDirApp = null, string logDirTmp = null, string prefix = null, bool? startExplicitly = null, bool createDirectory = false)
    {
        Exception result = null;
        try
        {
            if (startExplicitly != null)
                StartExplicitly = startExplicitly.Value;
            if (prefix != null)
                Prefix = prefix;

            if (logDirTmp != null)
                result = SetLogDirTmp(logDirTmp, createDirectory);
            if (logDirApp != null)
                result = SetLogDirApp(logDirApp, createDirectory);
        }
        catch (Exception ex)
        {
            result = ex;
        }
        return result;
    }

    public static List<string> GetWarnLines()
    {
        if (_backgroundTask != null)
        {
            Flush();
        }
        if (DirTmp != null && File.Exists(GetTmpFileName(Severity.Error)))
        {
            return
            File.ReadAllLines(GetTmpFileName(Severity.Error))
            .Where(x => x[0] != '#' &&
                        !x.Contains("log begin") &&
                        !x.Contains("log end"))
            .ToList();
        }
        return new List<string>();
    }

    public static void Start()
    {

        // Task already started
        if (_backgroundTask != null || StopEnqueingNewEntries || StopLoggingRequested)
            return;

        // Reset stopping flags
        StopEnqueingNewEntries = false;
        StopLoggingRequested = false;

        lock (_backgroundTaskSyncRoot)
        {
            if (_backgroundTask != null)
                return;

            // Create and start task
            _backgroundTask = new Task(WriteLogEntriesToFile, TaskCreationOptions.LongRunning);
            _backgroundTask.Start();
        }
    }

    public static void StartTmpLog(string dir)
    {
        SetLogDirTmp(dir);
        Flush();
        File.Delete(GetTmpFileName(Severity.Info));
        File.Delete(GetTmpFileName(Severity.Error));
        CreateLogHeader(Severity.Info);
        CreateLogHeader(Severity.Error);
        TmpLogStopped = false;
    }

    public static void Stop(bool flush = true)
    {
        // Stop enqueing new log entries.
        StopEnqueingNewEntries = true;

        // Useless to go on ...
        if (_backgroundTask == null)
            return;

        // Write pending entries to disk.
        if (flush)
            Flush();

        // Now tell the background task to stop.
        StopLoggingRequested = true;

        lock (_backgroundTaskSyncRoot)
        {
            if (_backgroundTask == null)
                return;

            // Wait for task to finish and set null then.
            _backgroundTask.Wait(1000);
            _backgroundTask = null;
        }
    }
    public static void StopTmpLog()
    {
        Flush();
        CreateLogFooter(Severity.Info);
        CreateLogFooter(Severity.Error);
        TmpLogStopped = true;
    }

    public static void Fatal(string message)
    {
        string logLine = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " -- " +
                                       string.Format("{0} | {1}", message, GetCaller());
        Enqueue(new LogEntry(logLine, Severity.Fatal));
    }

    public static void Error<T>(string message, T Par1, T Par2)
    {
#if DEBUG
        string logLine = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " -- " +
                                       string.Format("{1} | {0} | {2} | {3}", message, Par1, Par2, GetCaller());
#else
        string logLine = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " -- " + 
                                       string.Format("{1} | {0} | {2}", message, Par1, Par2);
#endif
        Enqueue(new LogEntry(logLine, Severity.Error));
    }

    public static void Information<T>(string message, T Par1)
    {
        string logLine = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " -- " +
                                       string.Format("{1} | {0}", message, Par1);
        Enqueue(new LogEntry(logLine, Severity.Info));
    }
    #endregion

    #region Private Methods
    private static Exception SetLogDirTmp(string logDir, bool createIfNotExisting = false)
    {
        if (string.IsNullOrEmpty(logDir))
            logDir = Directory.GetCurrentDirectory();

        try
        {
            _logDirTmp = new DirectoryInfo(logDir);

            if (!_logDirTmp.Exists)
            {
                if (createIfNotExisting)
                {
                    _logDirTmp.Create();
                }
                else
                {
                    throw new DirectoryNotFoundException(string.Format("Directory '{0}' does not exist!", _logDirTmp.FullName));
                }
            }
        }
        catch (Exception ex)
        {
            return ex;
        }

        return null;
    }

    private static Exception SetLogDirApp(string logDir, bool createIfNotExisting = false)
    {
        if (string.IsNullOrEmpty(logDir))
            logDir = Directory.GetCurrentDirectory();

        try
        {
            _logDirApp = new DirectoryInfo(logDir);

            if (!_logDirApp.Exists)
            {
                if (createIfNotExisting)
                {
                    _logDirApp.Create();
                }
                else
                {
                    throw new DirectoryNotFoundException(string.Format("Directory '{0}' does not exist!", _logDirApp.FullName));
                }
            }
        }
        catch (Exception ex)
        {
            return ex;
        }

        return null;
    }

    private static string GetCaller(int framesToSkip = 0)
    {
        string result = string.Empty;

        int i = 1;

        while (true)
        {
            // Walk up the stack trace ...
            var stackFrame = new StackFrame(i++, true);
            MethodBase methodBase = stackFrame.GetMethod();
            if (methodBase == null)
                break;

            // Here we're at the end - nomally we should never get that far 
            Type declaringType = methodBase.DeclaringType;
            if (declaringType == null)
                break;

            // Get class name and method of the current stack frame
            result = string.Format("{0}.{1} {2}", declaringType.Name, methodBase.Name, stackFrame.GetFileLineNumber());

            // Here, we're at the first method outside of SimpleLog class. 
            // This is the method that called the log method. We're done unless it is 
            // specified to skip additional frames and go further up the stack trace.
            if (declaringType != typeof(ErrLogger) && --framesToSkip < 0)
                break;
        }

        return result;
    }

    private static Exception WriteLogEntryToFile(string logLine, Severity severity)
    {
        if (logLine == null)
            return null;
        string FileName; // = Dir + "//" + defaultFileName;
        switch (severity)
        {
            case Severity.Info:
            case Severity.Error:
                FileName = GetTmpFileName(severity);
                break;
            case Severity.Fatal:
                FileName = GetFatalFileName(DateTime.Now);
                break;
            default:
                FileName = DirApp + "//" + defaultFileName;
                break;
        }
        const int secondsToWaitForFile = 5;


        if (Monitor.TryEnter(_logFileSyncRoot, new TimeSpan(0, 0, 0, secondsToWaitForFile)))
        {
            try
            {
                // Use filestream to be able to explicitly specify FileShare.None
                using (var fileStream = new FileStream(FileName, FileMode.Append, FileAccess.Write, FileShare.None))
                {
                    using (var streamWriter = new StreamWriter(fileStream))
                    {
                        streamWriter.WriteLine(logLine);
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                try
                {
                    ex.Data["Filename"] = FileName;
                }
                catch
                {
                }

                try
                {
                    WindowsIdentity user = WindowsIdentity.GetCurrent();
                    ex.Data["Username"] = user == null ? "unknown" : user.Name;
                }
                catch
                {
                }

                return ex;
            }
            finally
            {
                Monitor.Exit(_logFileSyncRoot);
            }
        }

        try
        {
            return new Exception(string.Format("Could not write to file '{0}', because it was blocked by another thread for more than {1} seconds.", FileName, secondsToWaitForFile));
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void WriteLogEntriesToFile()
    {
        while (!StopLoggingRequested)
        {
            // Get next log entry from queue
            LogEntry logEntry =
                _logEntryQueue.Count == 0 ? null : _logEntryQueue.Peek();
            if (logEntry == null)
            {
                // If queue is empty, sleep for a while and look again later.
                Thread.Sleep(100);
                continue;
            }

            // Try ten times to write the entry to the log file. Wait between tries, because the file could (hopefully) temporarily 
            // be locked by another application. When it didn't work out after ten tries, dequeue the entry anyway, i.e. the entry is lost then. 
            // This is necessary to ensure that the queue does not get too full and we run out of memory.
            for (int i = 0; i < 10; i++)
            {
                // Actually write entry to log file.
                Exception ex = WriteLogEntryToFile(logEntry.LogLine, logEntry.Severity);
                if (ex != null)
                {
                    using (var fileStream = new FileStream(DirTmp + "\\" + defaultFileName, FileMode.Append, FileAccess.Write, FileShare.None))
                    {
                        using (var streamWriter = new StreamWriter(fileStream))
                        {
                            streamWriter.WriteLine(ex.Message);
                        }
                    }
                }

                // When all is fine, we're done. Otherwise do not retry when queue is already getting full.
                if (ex == null || NumberOfLogEntriesWaitingToBeWrittenToFile > 1000)
                    break;

                // Only wait when queue is not already getting full.
                Thread.Sleep(100);
            }

            // Dequeue entry from the queue
            lock (_logEntryQueue)
            {
                if (_logEntryQueue.Count > 0)
                    _logEntryQueue.Dequeue();
            }
        }
    }

    private static void Enqueue(LogEntry logEntry)
    {
        // Stop enqueuing when instructed to do so
        if (StopEnqueingNewEntries)
            return;

        // Start logging if not already started, unless it is desired to start it explicitly
        if (!StartExplicitly && !LoggingStarted)
            Start();

        lock (_logEntryQueue)
        {
            // Stop enqueueing when the queue gets too full.
            if (_logEntryQueue.Count < 10000)
                _logEntryQueue.Enqueue(logEntry);
        }
    }

    private static void Flush()
    {
        // Background task not running? Nothing to do.
        if (!LoggingStarted)
            return;

        // Are there still items waiting to be written to disk?
        while (NumberOfLogEntriesWaitingToBeWrittenToFile > 0)
        {
            // Remember current number
            int lastNumber = NumberOfLogEntriesWaitingToBeWrittenToFile;

            // Wait some time to let background task do its work
            Thread.Sleep(222);

            // Didn't help? No log entries have been processed? We probably hang. 
            // Let it be to avoid waiting eternally.
            if (lastNumber == NumberOfLogEntriesWaitingToBeWrittenToFile)
                break;
        }
    }

    private static void CreateLogHeader(Severity severity)
    {
        WriteLogEntryToFile("# File: " + GetTmpFileName(severity), severity);
        WriteLogEntryToFile("# Date: " + DateTime.Now.Date.ToString("dd.MM.yyyy"), severity);
        WriteLogEntryToFile(DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " -- log begin", severity);
    }

    private static void CreateLogFooter(Severity severity)
    {
        WriteLogEntryToFile(DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " -- log end", severity);
    }
    private static string GetFatalFileName(DateTime dateTime)
    {
        return string.Format("{0}\\{1}_{2}.{3}", DirApp, "Fatal", dateTime.ToString("yyyyMMdd"), "log");
    }

    private static string GetTmpFileName(Severity severity)
    {
        string name = string.Join("_", new string[] { Prefix, severity.ToString().ToLower() }
                        .Where(s => !string.IsNullOrEmpty(s)));
        return string.Format("{0}\\{1}.{2}", DirTmp, name, "log");
    }
    #endregion
    private class LogEntry
    {
        public Severity Severity { get; }
        public string LogLine { get; }
        public LogEntry(string line, Severity severity)
        {
            this.LogLine = line;
            this.Severity = severity;
        }
    }
}
