using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Nexus.Agent.Acad.PropertyEngine;

/// <summary>Minimal IDispatch view: only what we need to get the type information.</summary>
[ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDispatchInfo
{
    [PreserveSig] int GetTypeInfoCount(out uint count);
    [PreserveSig] int GetTypeInfo(uint index, uint lcid, out ComTypes.ITypeInfo? typeInfo);
}

/// <summary>
/// The interface the Properties palette uses to put COM properties into categories
/// (General, Geometry, Text, ...). Implemented by AutoCAD's ActiveX objects.
/// </summary>
[ComImport, Guid("4D07FC10-F931-11CE-B001-00AA006884E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICategorizeProperties
{
    [PreserveSig] int MapPropertyToCategory(int dispid, out int propcat);
    [PreserveSig] int GetCategoryName(int propcat, int lcid, [MarshalAs(UnmanagedType.BStr)] out string? name);
}
