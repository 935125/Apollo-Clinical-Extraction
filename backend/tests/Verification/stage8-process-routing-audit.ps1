# Read only the routing/environment allowlist from the existing local API process.
# Never emit or persist other process environment values.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class RoutingEnvironmentAudit {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int rights, bool inherit, int pid);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int count, out IntPtr read);
  [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr handle, int kind, IntPtr[] info, int size, out int returned);
  static IntPtr Pointer(IntPtr handle, IntPtr address) { var b=new byte[IntPtr.Size]; IntPtr read; if(!ReadProcessMemory(handle,address,b,b.Length,out read)) throw new InvalidOperationException("Routing environment pointer unavailable"); return new IntPtr(BitConverter.ToInt64(b,0)); }
  public static Dictionary<string,string> Read(int pid) {
    var allowed=new HashSet<string>(new[]{"ASPNETCORE_ENVIRONMENT","DOTNET_ENVIRONMENT","Extraction__Provider","Extraction__FallbackProvider"},StringComparer.OrdinalIgnoreCase);
    var result=new Dictionary<string,string>(); var handle=OpenProcess(0x410,false,pid);
    if(handle==IntPtr.Zero) throw new InvalidOperationException("Routing environment access denied");
    try {
      var info=new IntPtr[6]; int returned;
      if(IntPtr.Size!=8 || NtQueryInformationProcess(handle,0,info,48,out returned)!=0) throw new InvalidOperationException("Unsupported process environment layout");
      var parameters=Pointer(handle,IntPtr.Add(info[1],0x20)); var environment=Pointer(handle,IntPtr.Add(parameters,0x80));
      var text=new StringBuilder();
      for(int offset=0;offset<1024*1024;offset+=1024) {
        var bytes=new byte[1024]; IntPtr read; ReadProcessMemory(handle,IntPtr.Add(environment,offset),bytes,bytes.Length,out read);
        if(read.ToInt64()==0) break; text.Append(Encoding.Unicode.GetString(bytes,0,(int)read));
        if(text.ToString().Contains("\0\0")) break;
      }
      foreach(var entry in text.ToString().Split('\0')) { int i=entry.IndexOf('='); if(i>0 && allowed.Contains(entry.Substring(0,i))) result[entry.Substring(0,i)]=entry.Substring(i+1); }
      return result;
    } finally { CloseHandle(handle); }
  }
}
'@
$process = Get-Process Apollo.Backend -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq 'D:\AI Extraction\backend\bin\Debug\net10.0\Apollo.Backend.exe' } | Select-Object -First 1
if (!$process) { throw 'Existing Apollo Debug process not found; no replacement process started.' }
$audit = [ordered]@{ processId=$process.Id; routingEnvironment=[RoutingEnvironmentAudit]::Read($process.Id); secretValuesIncluded=$false; processModified=$false }
$audit | ConvertTo-Json -Depth 5 | Set-Content 'D:\AI Extraction\docs\stage8-evidence\running-process-before.json'
$audit | ConvertTo-Json -Depth 5
