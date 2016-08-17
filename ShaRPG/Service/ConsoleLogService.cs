﻿#region

using System;

#endregion

namespace ShaRPG.Service {
    internal class ConsoleLogService : ILogService {
        public void Log(LogType logType, string content) {
            Console.WriteLine(logType + ": " + content);
        }
    }
}