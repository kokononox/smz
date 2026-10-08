#ifndef UNICODE
#define UNICODE 1
#endif
#ifndef _UNICODE
#define _UNICODE 1
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commdlg.h>
#include <shellapi.h>
#include <shlobj.h>
#include <sddl.h>
#include <stdio.h>
#include <wchar.h>
#include "embedded_sources.h"
#define BTN_SCAN 100
#define BTN_DETECT 101
#define WM_DONE (WM_APP+1)
static HWND input_box,status_box,scan_button,detect_button,main_window;
static HFONT ui_font;
static BOOL busy,test_mode;
static wchar_t input_text[16384],output_path[32768],failure_text[2048];
static DWORD result_code;
static void set_font(HWND h){SendMessageW(h,WM_SETFONT,(WPARAM)ui_font,TRUE);}
static HWND control(HWND parent,DWORD ex,const wchar_t *type,const wchar_t *text,DWORD style,int x,int y,int w,int h,int id){
 HWND c=CreateWindowExW(ex,type,text,WS_CHILD|WS_VISIBLE|style,x,y,w,h,parent,(HMENU)(INT_PTR)id,GetModuleHandleW(NULL),NULL);set_font(c);return c;
}
static BOOL write_bytes(const wchar_t *path,const unsigned char *bytes,DWORD count){
 HANDLE h=CreateFileW(path,GENERIC_WRITE,0,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);if(h==INVALID_HANDLE_VALUE)return FALSE;
 DWORD done=0;BOOL ok=WriteFile(h,bytes,count,&done,NULL)&&done==count;CloseHandle(h);return ok;
}
static BOOL make_private_dir(wchar_t *folder,size_t capacity){
 wchar_t temp[MAX_PATH];if(!GetTempPathW(MAX_PATH,temp)||!GetTempFileNameW(temp,L"WIR",0,folder))return FALSE;
 DeleteFileW(folder);HANDLE token;DWORD needed=0;
 if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return FALSE;
 GetTokenInformation(token,TokenUser,NULL,0,&needed);TOKEN_USER *user=(TOKEN_USER*)HeapAlloc(GetProcessHeap(),0,needed);
 if(!user){CloseHandle(token);return FALSE;}
 BOOL ok=GetTokenInformation(token,TokenUser,user,needed,&needed);LPWSTR sid=NULL;PSECURITY_DESCRIPTOR sd=NULL;
 wchar_t sddl[1024];
 if(ok&&ConvertSidToStringSidW(user->User.Sid,&sid)){
  _snwprintf(sddl,1024,L"D:P(A;;FA;;;%ls)(A;;FA;;;SY)",sid);
  ok=ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,SDDL_REVISION_1,&sd,NULL);
 }else ok=FALSE;
 if(ok){SECURITY_ATTRIBUTES sa={sizeof(sa),sd,FALSE};ok=CreateDirectoryW(folder,&sa);}
 if(sd)LocalFree(sd);if(sid)LocalFree(sid);HeapFree(GetProcessHeap(),0,user);CloseHandle(token);(void)capacity;return ok;
}
static DWORD WINAPI collect_thread(void *unused){
 (void)unused;wchar_t dir[MAX_PATH]={0},script[MAX_PATH],source[MAX_PATH],paths[MAX_PATH],log[MAX_PATH];
 HANDLE log_handle=INVALID_HANDLE_VALUE;PROCESS_INFORMATION pi={0};result_code=1;failure_text[0]=0;
 if(!make_private_dir(dir,MAX_PATH)){wcscpy(failure_text,L"ساخت پوشهٔ موقت خصوصی ناموفق بود.");goto done;}
 _snwprintf(script,MAX_PATH,L"%ls\\Collect-WindowsIdentity.ps1",dir);_snwprintf(source,MAX_PATH,L"%ls\\RegistryNames.cs",dir);
 _snwprintf(paths,MAX_PATH,L"%ls\\paths.txt",dir);_snwprintf(log,MAX_PATH,L"%ls\\engine.log",dir);
 if(!write_bytes(script,embedded_script,(DWORD)sizeof(embedded_script))||!write_bytes(source,embedded_cs,(DWORD)sizeof(embedded_cs))){wcscpy(failure_text,L"نوشتن موتور گزارش ناموفق بود.");goto cleanup;}
 int size=WideCharToMultiByte(CP_UTF8,0,input_text,-1,NULL,0,NULL,NULL);
 char *utf8=(char*)HeapAlloc(GetProcessHeap(),0,(SIZE_T)size+3);
 if(!utf8){wcscpy(failure_text,L"حافظه کافی نیست.");goto cleanup;}
 utf8[0]=(char)0xef;utf8[1]=(char)0xbb;utf8[2]=(char)0xbf;
 WideCharToMultiByte(CP_UTF8,0,input_text,-1,utf8+3,size,NULL,NULL);
 BOOL wrote=write_bytes(paths,(unsigned char*)utf8,(DWORD)size+2);HeapFree(GetProcessHeap(),0,utf8);
 if(!wrote){wcscpy(failure_text,L"نوشتن فهرست مسیرها ناموفق بود.");goto cleanup;}
 wchar_t system[MAX_PATH],powershell[MAX_PATH],command[34000];GetSystemDirectoryW(system,MAX_PATH);
 _snwprintf(powershell,MAX_PATH,L"%ls\\WindowsPowerShell\\v1.0\\powershell.exe",system);
 if(GetFileAttributesW(powershell)==INVALID_FILE_ATTRIBUTES){wcscpy(failure_text,L"Windows PowerShell 5.1 در این سیستم پیدا نشد.");goto cleanup;}
 SECURITY_ATTRIBUTES sa={sizeof(sa),NULL,TRUE};log_handle=CreateFileW(log,GENERIC_WRITE,FILE_SHARE_READ,&sa,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);
 if(log_handle==INVALID_HANDLE_VALUE){wcscpy(failure_text,L"ساخت لاگ موقت ناموفق بود.");goto cleanup;}
 HANDLE nul=CreateFileW(L"NUL",GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,&sa,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);
 STARTUPINFOW si={0};si.cb=sizeof(si);si.dwFlags=STARTF_USESTDHANDLES|STARTF_USESHOWWINDOW;si.wShowWindow=SW_HIDE;
 si.hStdOutput=log_handle;si.hStdError=log_handle;si.hStdInput=nul;
 _snwprintf(command,34000,L"\"%ls\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%ls\" -InputFile \"%ls\" -OutputFile \"%ls\"",powershell,script,paths,output_path);
 if(test_mode)_snwprintf(command,34000,L"\"%ls\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%ls\" -SelfTest",powershell,script);
 BOOL launched=CreateProcessW(powershell,command,NULL,NULL,TRUE,CREATE_NO_WINDOW,NULL,dir,&si,&pi);
 if(nul!=INVALID_HANDLE_VALUE)CloseHandle(nul);CloseHandle(log_handle);log_handle=INVALID_HANDLE_VALUE;
 if(!launched){wcscpy(failure_text,L"اجرای موتور گزارش ناموفق بود؛ محدودیت اجرای اسکریپت را بررسی کنید.");goto cleanup;}
 DWORD waited=WaitForSingleObject(pi.hProcess,180000);
 if(waited==WAIT_TIMEOUT){TerminateProcess(pi.hProcess,10);WaitForSingleObject(pi.hProcess,5000);result_code=10;wcscpy(failure_text,L"زمان بررسی تمام شد؛ گزارش ممکن است ناقص باشد.");}
 else GetExitCodeProcess(pi.hProcess,&result_code);
 CloseHandle(pi.hThread);CloseHandle(pi.hProcess);
 if(result_code!=0&&failure_text[0]==0){
  HANDLE h=CreateFileW(log,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);
  char bytes[4096];DWORD count=0;if(h!=INVALID_HANDLE_VALUE){ReadFile(h,bytes,sizeof(bytes)-1,&count,NULL);CloseHandle(h);}
  bytes[count]=0;MultiByteToWideChar(CP_UTF8,0,bytes,-1,failure_text,2048);
  if(failure_text[0]==0)wcscpy(failure_text,L"بررسی ناموفق بود؛ سیاست اجرای PowerShell یا دسترسی Administrator را بررسی کنید.");
 }
cleanup:
 if(log_handle!=INVALID_HANDLE_VALUE)CloseHandle(log_handle);
 DeleteFileW(script);DeleteFileW(source);DeleteFileW(paths);DeleteFileW(log);RemoveDirectoryW(dir);
done:
 PostMessageW(main_window,WM_DONE,0,0);return 0;
}
static void detect_paths(void){
 wchar_t drives[512],text[8192]={0};DWORD n=GetLogicalDriveStringsW(512,drives);if(!n||n>=512)return;
 for(wchar_t *p=drives;*p;p+=wcslen(p)+1){
  if(GetDriveTypeW(p)!=DRIVE_FIXED)continue;
  wchar_t test[MAX_PATH];_snwprintf(test,MAX_PATH,L"%lsWindows\\System32\\config\\SYSTEM",p);
  if(GetFileAttributesW(test)!=INVALID_FILE_ATTRIBUTES){wcscat(text,p);wcscat(text,L"Windows\r\n");}
 }
 if(text[0]==0){GetWindowsDirectoryW(text,8192);wcscat(text,L"\r\n");}
 SetWindowTextW(input_box,text);
}
static void start_collect(void){
 if(busy)return;GetWindowTextW(input_box,input_text,16384);
 if(wcslen(input_text)==0){MessageBoxW(main_window,L"حداقل یک مسیر ویندوز وارد کنید.",L"مسیر خالی",MB_ICONWARNING);return;}
 wcscpy(output_path,L"Windows-Identity-Report.txt");wchar_t desktop[MAX_PATH]={0};SHGetFolderPathW(NULL,CSIDL_DESKTOPDIRECTORY,NULL,SHGFP_TYPE_CURRENT,desktop);
 OPENFILENAMEW ofn={0};ofn.lStructSize=sizeof(ofn);ofn.hwndOwner=main_window;ofn.lpstrFilter=L"Text report (*.txt)\0*.txt\0\0";ofn.lpstrFile=output_path;
 ofn.nMaxFile=32768;ofn.lpstrDefExt=L"txt";ofn.lpstrInitialDir=desktop;ofn.Flags=OFN_OVERWRITEPROMPT|OFN_PATHMUSTEXIST|OFN_NOCHANGEDIR;
 if(!GetSaveFileNameW(&ofn))return;busy=TRUE;EnableWindow(scan_button,FALSE);EnableWindow(detect_button,FALSE);EnableWindow(input_box,FALSE);
 SetWindowTextW(status_box,L"در حال خواندن اطلاعات؛ تنظیمات بوت تغییر نمی‌کند...");
 HANDLE thread=CreateThread(NULL,0,collect_thread,NULL,0,NULL);
 if(thread)CloseHandle(thread);else{busy=FALSE;EnableWindow(scan_button,TRUE);EnableWindow(detect_button,TRUE);EnableWindow(input_box,TRUE);SetWindowTextW(status_box,L"شروع بررسی ناموفق بود.");}
}
static LRESULT CALLBACK window_proc(HWND h,UINT message,WPARAM w,LPARAM l){
 switch(message){
 case WM_CREATE:
  ui_font=CreateFontW(-18,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI");
  control(h,WS_EX_RTLREADING,L"STATIC",L"گزارش کمکی ویندوزها — نام کاربران، GUID و نام ورودی بوت",SS_RIGHT,20,15,660,32,0);
  control(h,WS_EX_RTLREADING,L"STATIC",L"هر مسیر در یک خط. برچسب روز/شب اختیاری: day=C:\\Windows یا night=D:\\Windows",SS_RIGHT,20,55,660,48,0);
  input_box=control(h,WS_EX_CLIENTEDGE,L"EDIT",L"",ES_MULTILINE|ES_AUTOVSCROLL|WS_VSCROLL|ES_WANTRETURN,20,110,660,190,0);SendMessageW(input_box,EM_SETLIMITTEXT,16000,0);
  detect_button=control(h,0,L"BUTTON",L"یافتن درایوهای ویندوز",BS_PUSHBUTTON,20,315,230,40,BTN_DETECT);
  scan_button=control(h,0,L"BUTTON",L"بررسی و ذخیرهٔ فایل TXT",BS_DEFPUSHBUTTON,370,315,310,40,BTN_SCAN);
  status_box=control(h,WS_EX_RTLREADING,L"STATIC",L"فقط خواندنی؛ نام کاربر هدف و نقش روز/شب را خودتان انتخاب کنید.",SS_RIGHT,20,375,660,60,0);detect_paths();return 0;
 case WM_COMMAND:if(LOWORD(w)==BTN_SCAN)start_collect();else if(LOWORD(w)==BTN_DETECT&&!busy)detect_paths();return 0;
 case WM_DONE:
  busy=FALSE;EnableWindow(scan_button,TRUE);EnableWindow(detect_button,TRUE);EnableWindow(input_box,TRUE);
  if(result_code==0){SetWindowTextW(status_box,L"گزارش TXT ذخیره شد؛ موارد نامشخص را داخل ویندوز مربوط بررسی کنید.");
   if(MessageBoxW(h,L"گزارش ذخیره شد. فایل متنی باز شود؟",L"پایان بررسی",MB_YESNO|MB_ICONINFORMATION)==IDYES)ShellExecuteW(h,L"open",output_path,NULL,NULL,SW_SHOWNORMAL);
  }else{SetWindowTextW(status_box,L"بررسی کامل نشد؛ اگر گزارشی ساخته شده، ممکن است ناقص باشد.");MessageBoxW(h,failure_text,L"خطای بررسی",MB_ICONERROR);}
  return 0;
 case WM_CLOSE:if(busy){MessageBoxW(h,L"لطفاً تا پایان بررسی صبر کنید.",L"در حال بررسی",MB_ICONINFORMATION);return 0;}DestroyWindow(h);return 0;
 case WM_DESTROY:if(ui_font)DeleteObject(ui_font);PostQuitMessage(0);return 0;
 default:return DefWindowProcW(h,message,w,l);
 }
}
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE previous,PWSTR args,int show){
 (void)previous;(void)args;
 int argc=0;LPWSTR *argv=CommandLineToArgvW(GetCommandLineW(),&argc);
 if(argv&&argc>=2&&(!wcscmp(argv[1],L"--self-test")||!wcscmp(argv[1],L"--scan"))){
  if(!IsUserAnAdmin()){LocalFree(argv);return 20;}
  test_mode=!wcscmp(argv[1],L"--self-test");
  if(test_mode){GetWindowsDirectoryW(input_text,16384);output_path[0]=0;}
  else if(argc==4&&wcslen(argv[2])<16384&&wcslen(argv[3])<32768){wcscpy(input_text,argv[2]);wcscpy(output_path,argv[3]);}
  else{LocalFree(argv);return 21;}
  LocalFree(argv);collect_thread(NULL);return (int)result_code;
 }
 if(argv)LocalFree(argv);
 if(!IsUserAnAdmin()){
  wchar_t exe[32768];GetModuleFileNameW(NULL,exe,32768);
  HINSTANCE r=ShellExecuteW(NULL,L"runas",exe,NULL,NULL,SW_SHOWNORMAL);
  if((INT_PTR)r<=32)MessageBoxW(NULL,L"برای خواندن BCD و ویندوزهای دیگر، اجرای Administrator لازم است.",L"Windows Identity Report",MB_ICONWARNING);return 0;
 }
 WNDCLASSW cls={0};cls.lpfnWndProc=window_proc;cls.hInstance=instance;cls.lpszClassName=L"WindowsIdentityReport";
 cls.hCursor=LoadCursorW(NULL,IDC_ARROW);cls.hbrBackground=(HBRUSH)(COLOR_WINDOW+1);
 if(!RegisterClassW(&cls))return 1;
 main_window=CreateWindowExW(0,cls.lpszClassName,L"Windows Identity Report — گزارش کمکی شیفت",WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX,
 CW_USEDEFAULT,CW_USEDEFAULT,720,490,NULL,NULL,instance,NULL);
 if(!main_window)return 1;ShowWindow(main_window,show);UpdateWindow(main_window);
 MSG msg;while(GetMessageW(&msg,NULL,0,0)>0){TranslateMessage(&msg);DispatchMessageW(&msg);}return 0;
}
