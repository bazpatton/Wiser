@echo off
REM Copy this file to launch-wiser-ntfy-monitor.bat (gitignored), edit secrets, then
REM point Task Scheduler at that .bat (see script header in wiser_ntfy_monitor.py).
cd /d "%~dp0"

set "WISER_IP=192.168.x.x"
set "WISER_SECRET=your-secret-here"
set "NTFY_TOPIC=your-private-topic-name"

REM Optional:
REM set "INTERVAL_SEC=300"
REM set "TEMP_ALERT_ABOVE_C=22"
REM set "TEMP_ALERT_BELOW_C=14"

py -3 wiser_ntfy_monitor.py
