// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.
namespace Microsoft.VisualStudio.FSharp.Interactive

#if DESIGNER
#r "FSharp.Compiler.Server.Shared.dll" 
#I @"C:\Program Files\Microsoft Visual Studio 2008 SDK\VisualStudioIntegration\Common\Assemblies"
#I @"C:\Program Files\Reference Assemblies\Microsoft\Framework\v3.5"
#r "System.Core.dll"
#r "system.windows.forms.dll"
#r "Microsoft.VisualStudio.OLE.Interop.dll"
#r "Microsoft.VisualStudio.Package.LanguageService.9.0.dll"
#r "Microsoft.VisualStudio.Shell.9.0.dll"
#r "Microsoft.VisualStudio.Shell.Interop.dll"
#r "Microsoft.VisualStudio.Shell.Interop.8.0.dll"
#r "Microsoft.VisualStudio.Shell.Interop.9.0.dll"
#r "Microsoft.VisualStudio.TextManager.Interop.dll"
#r "Microsoft.VisualStudio.TextManager.Interop.8.0.dll"
#endif  

open System
open System.IO
open System.Diagnostics
open System.Globalization
open System.Windows.Forms
open System.Runtime.InteropServices
open System.ComponentModel.Design
open Microsoft.Win32
open Microsoft.VisualStudio
open Microsoft.VisualStudio.Shell.Interop
open Microsoft.VisualStudio.OLE.Interop
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.TextManager.Interop

module internal AssemblyAttributes = 
    //[<assembly: System.Security.SecurityTransparent>]
    [<assembly:ComVisible(true)>]
    do()

module internal Guids =
#if FSI_SERVER_INTELLISENSE
    let enable_fsi_intellisense         = true
#endif
    
    // FSI Session command set
    let cmdIDFsiConsoleContextMenu      = 0x2100 
    let guidFsiConsoleCmdSet            = Guid("0E455B35-F2EB-431b-A0BE-B268D8A7D17F")
#if FX_ATLEAST_45
    let guidInteractiveCommands         = Microsoft.VisualStudio.VSConstants.VsStd11 
    let cmdIDSessionInterrupt           = int Microsoft.VisualStudio.VSConstants.VSStd11CmdID.InteractiveSessionInterrupt
    let cmdIDSessionRestart             = int Microsoft.VisualStudio.VSConstants.VSStd11CmdID.InteractiveSessionRestart
#else
    let guidInteractiveCommands         = guidFsiConsoleCmdSet
    let cmdIDSessionInterrupt           = 0x102
    let cmdIDSessionRestart             = 0x103
#endif
   
    // Command set for SendToInteractive
#if FX_ATLEAST_45
    // commands moved to VS Shell
    let guidInteractive                 = Microsoft.VisualStudio.VSConstants.VsStd11 
    let cmdIDSendSelection              = int Microsoft.VisualStudio.VSConstants.VSStd11CmdID.ExecuteSelectionInInteractive
    let guidInteractive2                = Microsoft.VisualStudio.VSConstants.VsStd11
#else
    // hybrid still uses own commands
    let guidInteractive                 = Guid("8B9BF77B-AF94-4588-8847-2EB2BFFD29EB")
    let cmdIDSendSelection              = 0x01
    let guidInteractive2                = Guid("B607E86C-A761-4685-8D98-71A3BB73233A")
#endif

    let guidFsiPackage                  = "eeeeeeee-9342-42f1-8ea9-42f0e8a6be55" // FSI-LINKAGE-POINT: when packaged here
    let guidFSharpProjectPkgString      = "91A04A73-4F2C-4E7C-AD38-C1A68E7DA05C" // FSI-LINKAGE-POINT: when packaged in project system

    let guidFsiLanguageService          = "35A5E6B8-4012-41fc-A652-2CDC56D74E9F"        // The FSI lang service
    let guidFsiSessionToolWindow        = "dee22b65-9761-4a26-8fb2-759b971d6dfc"

    // FSI Package command set
    let guidFsiPackageCmdSet            = Guid("0be3b0d7-4fc2-45bf-a168-957e8a8834d0")
    let cmdIDLaunchFsiToolWindow        = 0x101
    
    let nameFsiLanguageService          = "FSharpInteractive"                           // see Package registration attribute
    
    let guidFsharpLanguageService       = Guid("BC6DD5A5-D4D6-4dab-A00D-A51242DBAF1B")  // The F# source file lang service

module internal Util =
    /// Utility function to create an instance of a class from the local registry. [From Iron Python].
    let CreateObject (globalProvider:System.IServiceProvider) (classType:Type,interfaceType:Type) =
        // Follows IronPython sample. See ConsoleWindow.CreateObject
        let localRegistry = globalProvider.GetService(typeof<SLocalRegistry>) :?> ILocalRegistry   
        let mutable interfaceGuid = interfaceType.GUID    
        let res,interfacePointer = localRegistry.CreateInstance(classType.GUID, null, &interfaceGuid,uint32 CLSCTX.CLSCTX_INPROC_SERVER)
        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(res) |> ignore
        if interfacePointer = IntPtr.Zero then
            raise (new COMException("CanNotCreateObject"))
        else
            // Get a CLR object from the COM pointer
            let mutable obj = null
            try
                obj <- Marshal.GetObjectForIUnknown(interfacePointer)
            finally
                Marshal.Release(interfacePointer) |> ignore
            obj

    // CreateObject, using known type information.
    let CreateObjectT<'classT,'interfaceT> (provider:System.IServiceProvider) =
        let classType     = typeof<'classT>
        let interfaceType = typeof<'interfaceT>
        CreateObject provider (classType,interfaceType) :?> 'interfaceT

    let throwOnFailure0 (res)         = Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure res |> ignore; ()
    let throwOnFailure1 (res,a)       = Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure res |> ignore; a
    let throwOnFailure2 (res,a,b)     = Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure res |> ignore; (a,b)
    let throwOnFailure3 (res,a,b,c)   = Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure res |> ignore; (a,b,c)
    let throwOnFailure4 (res,a,b,c,d) = Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure res |> ignore; (a,b,c,d)
    

// History buffer.
// For now, follows the cmd.exe model.
type internal HistoryBuffer() =
    let lines   = new System.Collections.Generic.List<string>()    
    let mutable pointer = 0
    member this.Add(text:string)        = lines.Add(text); pointer <- lines.Count
    member this.CycleUp(text:string)    = if pointer-1 >= 0 then
                                            pointer <- pointer - 1
                                            Some lines.[pointer]
                                          else
                                            None
    member this.CycleDown(text:string)  = if pointer+1 < lines.Count then
                                            pointer <- pointer + 1
                                            Some lines.[pointer]
                                          else
                                            None



