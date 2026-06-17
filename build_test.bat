@echo off
dotnet build AcEvoFfbTuner.slnx -c Release 2>&1 | findstr /C:"error CS" /C:"Build succeeded" /C:"Build FAILED"
