#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#define PATH_CAPACITY 2048

static HMODULE bootstrapModule;
static volatile LONG started;

static LPCWSTR FileName(LPCWSTR path)
{
    LPCWSTR name = path;
    while (*path) {
        if (*path == L'\\' || *path == L'/') name = path + 1;
        ++path;
    }
    return name;
}

static BOOL IsSnowRunner(void)
{
    WCHAR host[PATH_CAPACITY];
    DWORD length = GetModuleFileNameW(NULL, host, PATH_CAPACITY);
    return length && length < PATH_CAPACITY && lstrcmpiW(FileName(host), L"SnowRunner.exe") == 0;
}

static BOOL Append(WCHAR *buffer, LPCWSTR text)
{
    DWORD used = (DWORD)lstrlenW(buffer);
    DWORD length = (DWORD)lstrlenW(text);
    if (used + length >= PATH_CAPACITY) return FALSE;
    lstrcpynW(buffer + used, text, (int)(PATH_CAPACITY - used));
    return TRUE;
}

static WCHAR *WriteNumber(WCHAR *destination, DWORD number)
{
    WCHAR reverse[12];
    DWORD count = 0;
    do { reverse[count++] = (WCHAR)(L'0' + number % 10); number /= 10; } while (number);
    while (count) *destination++ = reverse[--count];
    *destination = 0;
    return destination;
}

static void Clear(void *buffer, SIZE_T length)
{
    volatile BYTE *current = (volatile BYTE *)buffer;
    while (length--) *current++ = 0;
}

static void LogResult(LPCWSTR eventName, DWORD childPid, DWORD error)
{
    WCHAR path[PATH_CAPACITY];
    WCHAR message[512];
    WCHAR number[12];
    CHAR utf8[1024];
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", path, PATH_CAPACITY);
    HANDLE file;
    DWORD written;
    int bytes;
    if (!length || length >= PATH_CAPACITY || !Append(path, L"\\SnowRunnerApex5")) return;
    CreateDirectoryW(path, NULL);
    if (!Append(path, L"\\spotter-bootstrap.log")) return;
    lstrcpyW(message, L"{\"event\":\"");
    lstrcatW(message, eventName);
    lstrcatW(message, L"\",\"game_pid\":");
    WriteNumber(number, GetCurrentProcessId()); lstrcatW(message, number);
    lstrcatW(message, L",\"child_pid\":");
    WriteNumber(number, childPid); lstrcatW(message, number);
    lstrcatW(message, L",\"win32_error\":");
    WriteNumber(number, error); lstrcatW(message, number);
    lstrcatW(message, L"}\r\n");
    bytes = WideCharToMultiByte(CP_UTF8, 0, message, -1, utf8, sizeof(utf8), NULL, NULL);
    if (bytes <= 1) return;
    file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return;
    WriteFile(file, utf8, (DWORD)bytes - 1, &written, NULL);
    CloseHandle(file);
}

static DWORD WINAPI LaunchCompanion(LPVOID parameter)
{
    WCHAR directory[PATH_CAPACITY];
    WCHAR executable[PATH_CAPACITY];
    WCHAR command[PATH_CAPACITY];
    WCHAR pid[12];
    WCHAR *name;
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    DWORD length;
    DWORD failure;
    (void)parameter;
    length = GetModuleFileNameW(bootstrapModule, directory, PATH_CAPACITY);
    if (!length || length >= PATH_CAPACITY) { LogResult(L"module_path_failed", 0, GetLastError()); return 0; }
    name = (WCHAR *)FileName(directory);
    if (name == directory) { LogResult(L"module_directory_missing", 0, ERROR_BAD_PATHNAME); return 0; }
    *name = 0;
    lstrcpyW(executable, directory);
    if (!Append(executable, L"Spotter\\Apex5\\SnowRunnerApex5.exe")) {
        LogResult(L"companion_path_too_long", 0, ERROR_FILENAME_EXCED_RANGE); return 0;
    }
    command[0] = 0;
    WriteNumber(pid, GetCurrentProcessId());
    if (!Append(command, L"\"") || !Append(command, executable) ||
        !Append(command, L"\" --game-pid ") || !Append(command, pid)) {
        LogResult(L"command_too_long", 0, ERROR_FILENAME_EXCED_RANGE); return 0;
    }
    Clear(&startup, sizeof(startup));
    Clear(&process, sizeof(process));
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    if (!CreateProcessW(executable, command, NULL, NULL, FALSE, CREATE_NO_WINDOW,
            NULL, directory, &startup, &process)) {
        failure = GetLastError();
        LogResult(L"companion_start_failed", 0, failure);
        return 0;
    }
    LogResult(L"companion_started", process.dwProcessId, 0);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}

static void StartOnce(void)
{
    HANDLE thread;
    if (!IsSnowRunner() || InterlockedCompareExchange(&started, 1, 0) != 0) return;
    // Do not wait inside DllMain. Windows starts the worker after loader callbacks return.
    thread = CreateThread(NULL, 0, LaunchCompanion, NULL, 0, NULL);
    if (thread) CloseHandle(thread);
    else InterlockedExchange(&started, 0);
}

__declspec(dllexport) void InitializeASI(void) { StartOnce(); }
__declspec(dllexport) DWORD Apex5BootstrapVersion(void) { return 1; }

BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        bootstrapModule = module;
        StartOnce();
    }
    return TRUE;
}
