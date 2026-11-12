@ECHO OFF

REM Имя папки (проекта)
SET SUBDIR=src

REM Имя проекта
SET PROJECT=AgentPlatform

REM Имя приложения
SET NAME=WebApi

REM Путь 7zip
SET zip=C:\Program Files\7-Zip\7z.exe

REM Папка локального архива
SET BACKUP_DIR=F:\Backups\DEV\%PROJECT%
MKDIR %BACKUP_DIR%

SET HOUR=%time:~0,2%
SET HOUR=%HOUR: =0%
SET BACKUP_FILE=%BACKUP_DIR%\%NAME%_%DATE:~6,4%%DATE:~3,2%%DATE:~0,2%_%HOUR%%TIME:~3,2%%TIME:~6,2%.zip

REM Папка архива в облаке
SET ARCHIVE_DIR=S:\DEV
MKDIR %ARCHIVE_DIR%\%PROJECT%

REM Файл списка исключений файлов из архива
SET X_FILES=F:\Backups\DEV\CMD\x_any.txt


"%zip%" a -tzip -r0 -mx5 -xr@%X_FILES% -xr!logs -xr!plugins "%BACKUP_FILE%" "%CD%\%SUBDIR%\*"

SET ARCHIVE_FILE=%ARCHIVE_DIR%\%PROJECT%\%NAME%.zip

ECHO Copy %BACKUP_FILE% to %ARCHIVE_FILE%

COPY /Y /V %BACKUP_FILE% %ARCHIVE_FILE%

TIMEOUT /T 30
REM PAUSE
