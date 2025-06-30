using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime;

public static unsafe class IL2CPP
{
    private static readonly Dictionary<string, IntPtr> ourImagesMap = new();

    static IL2CPP()
    {
        InitIL2CPPExports();
        var domain = il2cpp_domain_get();
        if (domain == IntPtr.Zero)
        {
            Logger.Instance.LogError("No il2cpp domain found; sad!");
            return;
        }

        uint assembliesCount = 0;
        var assemblies = il2cpp_domain_get_assemblies(domain, ref assembliesCount);
        for (var i = 0; i < assembliesCount; i++)
        {
            var image = il2cpp_assembly_get_image(assemblies[i]);
            var name = Marshal.PtrToStringAnsi(il2cpp_image_get_name(image));
            ourImagesMap[name] = image;
        }
    }

    internal static IntPtr GetIl2CppImage(string name)
    {
        if (ourImagesMap.ContainsKey(name)) return ourImagesMap[name];
        return IntPtr.Zero;
    }

    internal static IntPtr[] GetIl2CppImages()
    {
        return ourImagesMap.Values.ToArray();
    }

    public static IntPtr GetIl2CppClass(string assemblyName, string namespaze, string className)
    {
        if (!ourImagesMap.TryGetValue(assemblyName, out var image))
        {
            Logger.Instance.LogError("Assembly {AssemblyName} is not registered in il2cpp", assemblyName);
            return IntPtr.Zero;
        }

        var clazz = il2cpp_class_from_name(image, namespaze, className);
        return clazz;
    }

    public static IntPtr GetIl2CppField(IntPtr clazz, string fieldName)
    {
        if (clazz == IntPtr.Zero) return IntPtr.Zero;

        var field = il2cpp_class_get_field_from_name(clazz, fieldName);
        if (field == IntPtr.Zero)
            Logger.Instance.LogError(
                "Field {FieldName} was not found on class {ClassName}", fieldName, Marshal.PtrToStringUTF8(il2cpp_class_get_name(clazz)));
        return field;
    }

    public static IntPtr GetIl2CppMethodByToken(IntPtr clazz, int token)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(token.ToString());

        var iter = IntPtr.Zero;
        IntPtr method;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
            if (il2cpp_method_get_token(method) == token)
                return method;

        var className = Marshal.PtrToStringAnsi(il2cpp_class_get_name(clazz));
        Logger.Instance.LogTrace("Unable to find method {ClassName}::{Token}", className, token);

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + token);
    }

    public static IntPtr GetIl2CppMethod(IntPtr clazz, bool isGeneric, string methodName, string returnTypeName,
        params string[] argTypes)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(methodName + "(" + string.Join(", ", argTypes) +
                                                                   ")");

        returnTypeName = Regex.Replace(returnTypeName, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        for (var index = 0; index < argTypes.Length; index++)
        {
            var argType = argTypes[index];
            argTypes[index] = Regex.Replace(argType, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        }

        var methodsSeen = 0;
        var lastMethod = IntPtr.Zero;
        var iter = IntPtr.Zero;
        IntPtr method;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (Marshal.PtrToStringAnsi(il2cpp_method_get_name(method)) != methodName)
                continue;

            if (il2cpp_method_get_param_count(method) != argTypes.Length)
                continue;

            if (il2cpp_method_is_generic(method) != isGeneric)
                continue;

            var returnType = il2cpp_method_get_return_type(method);
            var returnTypeNameActual = Marshal.PtrToStringAnsi(il2cpp_type_get_name(returnType));
            if (returnTypeNameActual != returnTypeName)
                continue;

            methodsSeen++;
            lastMethod = method;

            var badType = false;
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = Marshal.PtrToStringAnsi(il2cpp_type_get_name(paramType));
                if (typeName != argTypes[i])
                {
                    badType = true;
                    break;
                }
            }

            if (badType) continue;

            return method;
        }

        var className = Marshal.PtrToStringAnsi(il2cpp_class_get_name(clazz));

        if (methodsSeen == 1)
        {
            Logger.Instance.LogTrace(
                "Method {ClassName}::{MethodName} was stubbed with a random matching method of the same name", className, methodName);
            Logger.Instance.LogTrace(
                "Stubby return type/target: {LastMethod} / {ReturnTypeName}", Marshal.PtrToStringUTF8(il2cpp_type_get_name(il2cpp_method_get_return_type(lastMethod))), returnTypeName);
            Logger.Instance.LogTrace("Stubby parameter types/targets follow:");
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(lastMethod, (uint)i);
                var typeName = Marshal.PtrToStringAnsi(il2cpp_type_get_name(paramType));
                Logger.Instance.LogTrace("    {TypeName} / {ArgType}", typeName, argTypes[i]);
            }

            return lastMethod;
        }

        Logger.Instance.LogTrace("Unable to find method {ClassName}::{MethodName}; signature follows", className, methodName);
        Logger.Instance.LogTrace("    return {ReturnTypeName}", returnTypeName);
        foreach (var argType in argTypes)
            Logger.Instance.LogTrace("    {ArgType}", argType);
        Logger.Instance.LogTrace("Available methods of this name follow:");
        iter = IntPtr.Zero;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (Marshal.PtrToStringAnsi(il2cpp_method_get_name(method)) != methodName)
                continue;

            var nParams = il2cpp_method_get_param_count(method);
            Logger.Instance.LogTrace("Method starts");
            Logger.Instance.LogTrace(
                "     return {MethodTypeName}", Marshal.PtrToStringUTF8(il2cpp_type_get_name(il2cpp_method_get_return_type(method))));
            for (var i = 0; i < nParams; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = Marshal.PtrToStringAnsi(il2cpp_type_get_name(paramType));
                Logger.Instance.LogTrace("    {TypeName}", typeName);
            }

            return method;
        }

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + methodName + "(" +
                                                               string.Join(", ", argTypes) + ")");
    }

    public static string? Il2CppStringToManaged(IntPtr il2CppString)
    {
        if (il2CppString == IntPtr.Zero) return null;

        var length = il2cpp_string_length(il2CppString);
        var chars = il2cpp_string_chars(il2CppString);

        return new string(chars, 0, length);
    }

    public static IntPtr ManagedStringToIl2Cpp(string? str)
    {
        if (str == null) return IntPtr.Zero;

        fixed (char* chars = str)
        {
            return il2cpp_string_new_utf16(chars, str.Length);
        }
    }

    public static IntPtr Il2CppObjectBaseToPtr(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? IntPtr.Zero;
    }

    public static IntPtr Il2CppObjectBaseToPtrNotNull(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? throw new NullReferenceException();
    }

    public static IntPtr GetIl2CppNestedType(IntPtr enclosingType, string nestedTypeName)
    {
        if (enclosingType == IntPtr.Zero) return IntPtr.Zero;

        var iter = IntPtr.Zero;
        IntPtr nestedTypePtr;
        if (il2cpp_class_is_inflated(enclosingType))
        {
            Logger.Instance.LogTrace("Original class was inflated, falling back to reflection");

            return RuntimeReflectionHelper.GetNestedTypeViaReflection(enclosingType, nestedTypeName);
        }

        while ((nestedTypePtr = il2cpp_class_get_nested_types(enclosingType, ref iter)) != IntPtr.Zero)
            if (Marshal.PtrToStringAnsi(il2cpp_class_get_name(nestedTypePtr)) == nestedTypeName)
                return nestedTypePtr;

        Logger.Instance.LogError(
            "Nested type {NestedTypeName} on {EnclosingTypeName} not found!", nestedTypeName, Marshal.PtrToStringUTF8(il2cpp_class_get_name(enclosingType)));

        return IntPtr.Zero;
    }

    public static void ThrowIfNull(object arg)
    {
        if (arg == null)
            throw new NullReferenceException();
    }

    public static T ResolveICall<T>(string signature) where T : Delegate
    {
        var icallPtr = il2cpp_resolve_icall(signature);
        if (icallPtr == IntPtr.Zero)
        {
            Logger.Instance.LogTrace("ICall {Signature} not resolved", signature);
            return GenerateDelegateForMissingICall<T>(signature);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(icallPtr);
    }

    private static T GenerateDelegateForMissingICall<T>(string signature) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!;

        var trampoline = new DynamicMethod("(missing icall delegate) " + typeof(T).FullName,
            invoke.ReturnType, invoke.GetParameters().Select(it => it.ParameterType).ToArray(), typeof(IL2CPP), true);
        var bodyBuilder = trampoline.GetILGenerator();

        bodyBuilder.Emit(OpCodes.Ldstr, $"ICall with signature {signature} was not resolved");
        bodyBuilder.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor(new[] { typeof(string) })!);
        bodyBuilder.Emit(OpCodes.Throw);

        return (T)trampoline.CreateDelegate(typeof(T));
    }

    public static T? PointerToValueGeneric<T>(IntPtr objectPointer, bool isFieldPointer, bool valueTypeWouldBeBoxed)
    {
        if (isFieldPointer)
        {
            if (il2cpp_class_is_valuetype(Il2CppClassPointerStore<T>.NativeClassPtr))
                objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);
            else
                objectPointer = *(IntPtr*)objectPointer;
        }

        if (!valueTypeWouldBeBoxed && il2cpp_class_is_valuetype(Il2CppClassPointerStore<T>.NativeClassPtr))
            objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);

        if (typeof(T) == typeof(string))
            return (T)(object)Il2CppStringToManaged(objectPointer);

        if (objectPointer == IntPtr.Zero)
            return default;

        if (typeof(T).IsValueType)
            return Il2CppObjectBase.UnboxUnsafe<T>(objectPointer);

        return Il2CppObjectPool.Get<T>(objectPointer);
    }

    public static string RenderTypeName<T>(bool addRefMarker = false)
    {
        return RenderTypeName(typeof(T), addRefMarker);
    }

    public static string RenderTypeName(Type t, bool addRefMarker = false)
    {
        if (addRefMarker) return RenderTypeName(t) + "&";
        if (t.IsArray) return RenderTypeName(t.GetElementType()) + "[]";
        if (t.IsByRef) return RenderTypeName(t.GetElementType()) + "&";
        if (t.IsPointer) return RenderTypeName(t.GetElementType()) + "*";
        if (t.IsGenericParameter) return t.Name;

        if (t.IsGenericType)
        {
            if (t.TypeHasIl2CppArrayBase())
                return RenderTypeName(t.GetGenericArguments()[0]) + "[]";

            var builder = new StringBuilder();
            builder.Append(t.GetGenericTypeDefinition().FullNameObfuscated().TrimIl2CppPrefix());
            builder.Append('<');
            var genericArguments = t.GetGenericArguments();
            for (var i = 0; i < genericArguments.Length; i++)
            {
                if (i != 0) builder.Append(',');
                builder.Append(RenderTypeName(genericArguments[i]));
            }

            builder.Append('>');
            return builder.ToString();
        }

        if (t == typeof(Il2CppStringArray))
            return "System.String[]";

        return t.FullNameObfuscated().TrimIl2CppPrefix();
    }

    private static string FullNameObfuscated(this Type t)
    {
        var obfuscatedNameAnnotations = t.GetCustomAttribute<ObfuscatedNameAttribute>();
        if (obfuscatedNameAnnotations == null) return t.FullName;
        return obfuscatedNameAnnotations.ObfuscatedName;
    }

    private static string TrimIl2CppPrefix(this string s)
    {
        return s.StartsWith("Il2Cpp") ? s.Substring("Il2Cpp".Length) : s;
    }

    private static bool TypeHasIl2CppArrayBase(this Type type)
    {
        if (type == null) return false;
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
        if (type == typeof(Il2CppArrayBase<>)) return true;
        return TypeHasIl2CppArrayBase(type.BaseType);
    }

    // this is called if there's no actual il2cpp_gc_wbarrier_set_field()
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FieldWriteWbarrierStub(IntPtr obj, IntPtr targetAddress, IntPtr value)
    {
        // ignore obj
        *(IntPtr*)targetAddress = value;
    }

    // IL2CPP Functions
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntPtr il2cpp_method_get_from_reflection(IntPtr method)
    {
        if (UnityVersionHandler.HasGetMethodFromReflection) return _il2cpp_method_get_from_reflection(method);
        Il2CppReflectionMethod* reflectionMethod = (Il2CppReflectionMethod*)method;
        return (IntPtr)reflectionMethod->method;
    }





    private static IntPtr GameAssemblyHandle;




    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_init_Delegate(IntPtr domain_name);
    private static il2cpp_init_Delegate handle_il2cpp_init;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_init_utf16_Delegate(IntPtr domain_name);
    private static il2cpp_init_utf16_Delegate handle_il2cpp_init_utf16;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_shutdown_Delegate();
    private static il2cpp_shutdown_Delegate handle_il2cpp_shutdown;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_set_config_dir_Delegate(IntPtr config_path);
    private static il2cpp_set_config_dir_Delegate handle_il2cpp_set_config_dir;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_set_data_dir_Delegate(IntPtr data_path);
    private static il2cpp_set_data_dir_Delegate handle_il2cpp_set_data_dir;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_set_temp_dir_Delegate(IntPtr temp_path);
    private static il2cpp_set_temp_dir_Delegate handle_il2cpp_set_temp_dir;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_set_commandline_arguments_Delegate(int argc, IntPtr argv, IntPtr basedir);
    private static il2cpp_set_commandline_arguments_Delegate handle_il2cpp_set_commandline_arguments;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_set_commandline_arguments_utf16_Delegate(int argc, IntPtr argv, IntPtr basedir);
    private static il2cpp_set_commandline_arguments_utf16_Delegate handle_il2cpp_set_commandline_arguments_utf16;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_set_config_utf16_Delegate(IntPtr executablePath);
    private static il2cpp_set_config_utf16_Delegate handle_il2cpp_set_config_utf16;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_set_config_Delegate(IntPtr executablePath);
    private static il2cpp_set_config_Delegate handle_il2cpp_set_config;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_set_memory_callbacks_Delegate(IntPtr callbacks);
    private static il2cpp_set_memory_callbacks_Delegate handle_il2cpp_set_memory_callbacks;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_get_corlib_Delegate();
    private static il2cpp_get_corlib_Delegate handle_il2cpp_get_corlib;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_add_internal_call_Delegate(IntPtr name, IntPtr method);
    private static il2cpp_add_internal_call_Delegate handle_il2cpp_add_internal_call;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_resolve_icall_Delegate([MarshalAs(UnmanagedType.LPStr)] string name);
    private static il2cpp_resolve_icall_Delegate handle_il2cpp_resolve_icall;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_alloc_Delegate(uint size);
    private static il2cpp_alloc_Delegate handle_il2cpp_alloc;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_free_Delegate(IntPtr ptr);
    private static il2cpp_free_Delegate handle_il2cpp_free;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_array_class_get_Delegate(IntPtr element_class, uint rank);
    private static il2cpp_array_class_get_Delegate handle_il2cpp_array_class_get;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_array_length_Delegate(IntPtr array);
    private static il2cpp_array_length_Delegate handle_il2cpp_array_length;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_array_get_byte_length_Delegate(IntPtr array);
    private static il2cpp_array_get_byte_length_Delegate handle_il2cpp_array_get_byte_length;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_array_new_Delegate(IntPtr elementTypeInfo, ulong length);
    private static il2cpp_array_new_Delegate handle_il2cpp_array_new;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_array_new_specific_Delegate(IntPtr arrayTypeInfo, ulong length);
    private static il2cpp_array_new_specific_Delegate handle_il2cpp_array_new_specific;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_array_new_full_Delegate(IntPtr array_class, ref ulong lengths, ref ulong lower_bounds);
    private static il2cpp_array_new_full_Delegate handle_il2cpp_array_new_full;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_bounded_array_class_get_Delegate(IntPtr element_class, uint rank, [MarshalAs(UnmanagedType.I1)] bool bounded);
    private static il2cpp_bounded_array_class_get_Delegate handle_il2cpp_bounded_array_class_get;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_array_element_size_Delegate(IntPtr array_class);
    private static il2cpp_array_element_size_Delegate handle_il2cpp_array_element_size;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_assembly_get_image_Delegate(IntPtr assembly);
    private static il2cpp_assembly_get_image_Delegate handle_il2cpp_assembly_get_image;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_enum_basetype_Delegate(IntPtr klass);
    private static il2cpp_class_enum_basetype_Delegate handle_il2cpp_class_enum_basetype;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_is_generic_Delegate(IntPtr klass);
    private static il2cpp_class_is_generic_Delegate handle_il2cpp_class_is_generic;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_is_inflated_Delegate(IntPtr klass);
    private static il2cpp_class_is_inflated_Delegate handle_il2cpp_class_is_inflated;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_is_assignable_from_Delegate(IntPtr klass, IntPtr oklass);
    private static il2cpp_class_is_assignable_from_Delegate handle_il2cpp_class_is_assignable_from;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_is_subclass_of_Delegate(IntPtr klass, IntPtr klassc, [MarshalAs(UnmanagedType.I1)] bool check_interfaces);
    private static il2cpp_class_is_subclass_of_Delegate handle_il2cpp_class_is_subclass_of;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_has_parent_Delegate(IntPtr klass, IntPtr klassc);
    private static il2cpp_class_has_parent_Delegate handle_il2cpp_class_has_parent;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_from_il2cpp_type_Delegate(IntPtr type);
    private static il2cpp_class_from_il2cpp_type_Delegate handle_il2cpp_class_from_il2cpp_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_from_name_Delegate(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string namespaze, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    private static il2cpp_class_from_name_Delegate handle_il2cpp_class_from_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_from_system_type_Delegate(IntPtr type);
    private static il2cpp_class_from_system_type_Delegate handle_il2cpp_class_from_system_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_element_class_Delegate(IntPtr klass);
    private static il2cpp_class_get_element_class_Delegate handle_il2cpp_class_get_element_class;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_events_Delegate(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_events_Delegate handle_il2cpp_class_get_events;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_fields_Delegate(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_fields_Delegate handle_il2cpp_class_get_fields;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_nested_types_Delegate(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_nested_types_Delegate handle_il2cpp_class_get_nested_types;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_interfaces_Delegate(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_interfaces_Delegate handle_il2cpp_class_get_interfaces;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_properties_Delegate(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_properties_Delegate handle_il2cpp_class_get_properties;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_property_from_name_Delegate(IntPtr klass, IntPtr name);
    private static il2cpp_class_get_property_from_name_Delegate handle_il2cpp_class_get_property_from_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_field_from_name_Delegate(IntPtr klass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    private static il2cpp_class_get_field_from_name_Delegate handle_il2cpp_class_get_field_from_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_methods_Delegate(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_methods_Delegate handle_il2cpp_class_get_methods;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_method_from_name_Delegate(IntPtr klass, [MarshalAs(UnmanagedType.LPStr)] string name, int argsCount);
    private static il2cpp_class_get_method_from_name_Delegate handle_il2cpp_class_get_method_from_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_name_Delegate(IntPtr klass);
    private static il2cpp_class_get_name_Delegate handle_il2cpp_class_get_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_namespace_Delegate(IntPtr klass);
    private static il2cpp_class_get_namespace_Delegate handle_il2cpp_class_get_namespace;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_parent_Delegate(IntPtr klass);
    private static il2cpp_class_get_parent_Delegate handle_il2cpp_class_get_parent;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_declaring_type_Delegate(IntPtr klass);
    private static il2cpp_class_get_declaring_type_Delegate handle_il2cpp_class_get_declaring_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_class_instance_size_Delegate(IntPtr klass);
    private static il2cpp_class_instance_size_Delegate handle_il2cpp_class_instance_size;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_class_num_fields_Delegate(IntPtr enumKlass);
    private static il2cpp_class_num_fields_Delegate handle_il2cpp_class_num_fields;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_is_valuetype_Delegate(IntPtr klass);
    private static il2cpp_class_is_valuetype_Delegate handle_il2cpp_class_is_valuetype;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_class_value_size_Delegate(IntPtr klass, ref uint align);
    private static il2cpp_class_value_size_Delegate handle_il2cpp_class_value_size;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_is_blittable_Delegate(IntPtr klass);
    private static il2cpp_class_is_blittable_Delegate handle_il2cpp_class_is_blittable;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_class_get_flags_Delegate(IntPtr klass);
    private static il2cpp_class_get_flags_Delegate handle_il2cpp_class_get_flags;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_is_abstract_Delegate(IntPtr klass);
    private static il2cpp_class_is_abstract_Delegate handle_il2cpp_class_is_abstract;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_is_interface_Delegate(IntPtr klass);
    private static il2cpp_class_is_interface_Delegate handle_il2cpp_class_is_interface;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_class_array_element_size_Delegate(IntPtr klass);
    private static il2cpp_class_array_element_size_Delegate handle_il2cpp_class_array_element_size;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_from_type_Delegate(IntPtr type);
    private static il2cpp_class_from_type_Delegate handle_il2cpp_class_from_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_type_Delegate(IntPtr klass);
    private static il2cpp_class_get_type_Delegate handle_il2cpp_class_get_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_class_get_type_token_Delegate(IntPtr klass);
    private static il2cpp_class_get_type_token_Delegate handle_il2cpp_class_get_type_token;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_has_attribute_Delegate(IntPtr klass, IntPtr attr_class);
    private static il2cpp_class_has_attribute_Delegate handle_il2cpp_class_has_attribute;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_has_references_Delegate(IntPtr klass);
    private static il2cpp_class_has_references_Delegate handle_il2cpp_class_has_references;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_class_is_enum_Delegate(IntPtr klass);
    private static il2cpp_class_is_enum_Delegate handle_il2cpp_class_is_enum;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_image_Delegate(IntPtr klass);
    private static il2cpp_class_get_image_Delegate handle_il2cpp_class_get_image;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_class_get_assemblyname_Delegate(IntPtr klass);
    private static il2cpp_class_get_assemblyname_Delegate handle_il2cpp_class_get_assemblyname;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_class_get_rank_Delegate(IntPtr klass);
    private static il2cpp_class_get_rank_Delegate handle_il2cpp_class_get_rank;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_class_get_bitmap_size_Delegate(IntPtr klass);
    private static il2cpp_class_get_bitmap_size_Delegate handle_il2cpp_class_get_bitmap_size;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_class_get_bitmap_Delegate(IntPtr klass, ref uint bitmap);
    private static il2cpp_class_get_bitmap_Delegate handle_il2cpp_class_get_bitmap;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_stats_dump_to_file_Delegate(IntPtr path);
    private static il2cpp_stats_dump_to_file_Delegate handle_il2cpp_stats_dump_to_file;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_domain_get_Delegate();
    private static il2cpp_domain_get_Delegate handle_il2cpp_domain_get;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_domain_assembly_open_Delegate(IntPtr domain, IntPtr name);
    private static il2cpp_domain_assembly_open_Delegate handle_il2cpp_domain_assembly_open;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr* il2cpp_domain_get_assemblies_Delegate(IntPtr domain, ref uint size);
    private static il2cpp_domain_get_assemblies_Delegate handle_il2cpp_domain_get_assemblies;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_exception_from_name_msg_Delegate(IntPtr image, IntPtr name_space, IntPtr name, IntPtr msg);
    private static il2cpp_exception_from_name_msg_Delegate handle_il2cpp_exception_from_name_msg;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_get_exception_argument_null_Delegate(IntPtr arg);
    private static il2cpp_get_exception_argument_null_Delegate handle_il2cpp_get_exception_argument_null;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_format_exception_Delegate(IntPtr ex, void* message, int message_size);
    private static il2cpp_format_exception_Delegate handle_il2cpp_format_exception;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_format_stack_trace_Delegate(IntPtr ex, void* output, int output_size);
    private static il2cpp_format_stack_trace_Delegate handle_il2cpp_format_stack_trace;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_unhandled_exception_Delegate(IntPtr ex);
    private static il2cpp_unhandled_exception_Delegate handle_il2cpp_unhandled_exception;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_field_get_flags_Delegate(IntPtr field);
    private static il2cpp_field_get_flags_Delegate handle_il2cpp_field_get_flags;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_field_get_name_Delegate(IntPtr field);
    private static il2cpp_field_get_name_Delegate handle_il2cpp_field_get_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_field_get_parent_Delegate(IntPtr field);
    private static il2cpp_field_get_parent_Delegate handle_il2cpp_field_get_parent;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_field_get_offset_Delegate(IntPtr field);
    private static il2cpp_field_get_offset_Delegate handle_il2cpp_field_get_offset;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_field_get_type_Delegate(IntPtr field);
    private static il2cpp_field_get_type_Delegate handle_il2cpp_field_get_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_field_get_value_Delegate(IntPtr obj, IntPtr field, void* value);
    private static il2cpp_field_get_value_Delegate handle_il2cpp_field_get_value;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_field_get_value_object_Delegate(IntPtr field, IntPtr obj);
    private static il2cpp_field_get_value_object_Delegate handle_il2cpp_field_get_value_object;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_field_has_attribute_Delegate(IntPtr field, IntPtr attr_class);
    private static il2cpp_field_has_attribute_Delegate handle_il2cpp_field_has_attribute;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_field_set_value_Delegate(IntPtr obj, IntPtr field, void* value);
    private static il2cpp_field_set_value_Delegate handle_il2cpp_field_set_value;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_field_static_get_value_Delegate(IntPtr field, void* value);
    private static il2cpp_field_static_get_value_Delegate handle_il2cpp_field_static_get_value;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_field_static_set_value_Delegate(IntPtr field, void* value);
    private static il2cpp_field_static_set_value_Delegate handle_il2cpp_field_static_set_value;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_field_set_value_object_Delegate(IntPtr instance, IntPtr field, IntPtr value);
    private static il2cpp_field_set_value_object_Delegate handle_il2cpp_field_set_value_object;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_gc_collect_Delegate(int maxGenerations);
    private static il2cpp_gc_collect_Delegate handle_il2cpp_gc_collect;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_gc_collect_a_little_Delegate();
    private static il2cpp_gc_collect_a_little_Delegate handle_il2cpp_gc_collect_a_little;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_gc_disable_Delegate();
    private static il2cpp_gc_disable_Delegate handle_il2cpp_gc_disable;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_gc_enable_Delegate();
    private static il2cpp_gc_enable_Delegate handle_il2cpp_gc_enable;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_gc_is_disabled_Delegate();
    private static il2cpp_gc_is_disabled_Delegate handle_il2cpp_gc_is_disabled;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long il2cpp_gc_get_used_size_Delegate();
    private static il2cpp_gc_get_used_size_Delegate handle_il2cpp_gc_get_used_size;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long il2cpp_gc_get_heap_size_Delegate();
    private static il2cpp_gc_get_heap_size_Delegate handle_il2cpp_gc_get_heap_size;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_gc_wbarrier_set_field_Delegate(IntPtr obj, IntPtr targetAddress, IntPtr gcObj);
    private static il2cpp_gc_wbarrier_set_field_Delegate handle_il2cpp_gc_wbarrier_set_field;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_gchandle_new_Delegate(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool pinned);
    private static il2cpp_gchandle_new_Delegate handle_il2cpp_gchandle_new;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_gchandle_new_weakref_Delegate(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool track_resurrection);
    private static il2cpp_gchandle_new_weakref_Delegate handle_il2cpp_gchandle_new_weakref;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_gchandle_get_target_Delegate(IntPtr gchandle);
    private static il2cpp_gchandle_get_target_Delegate handle_il2cpp_gchandle_get_target;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_gchandle_free_Delegate(IntPtr gchandle);
    private static il2cpp_gchandle_free_Delegate handle_il2cpp_gchandle_free;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_unity_liveness_calculation_begin_Delegate(IntPtr filter, int max_object_count, IntPtr callback, IntPtr userdata, IntPtr onWorldStarted, IntPtr onWorldStopped);
    private static il2cpp_unity_liveness_calculation_begin_Delegate handle_il2cpp_unity_liveness_calculation_begin;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_unity_liveness_calculation_end_Delegate(IntPtr state);
    private static il2cpp_unity_liveness_calculation_end_Delegate handle_il2cpp_unity_liveness_calculation_end;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_unity_liveness_calculation_from_root_Delegate(IntPtr root, IntPtr state);
    private static il2cpp_unity_liveness_calculation_from_root_Delegate handle_il2cpp_unity_liveness_calculation_from_root;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_unity_liveness_calculation_from_statics_Delegate(IntPtr state);
    private static il2cpp_unity_liveness_calculation_from_statics_Delegate handle_il2cpp_unity_liveness_calculation_from_statics;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_method_get_return_type_Delegate(IntPtr method);
    private static il2cpp_method_get_return_type_Delegate handle_il2cpp_method_get_return_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_method_get_declaring_type_Delegate(IntPtr method);
    private static il2cpp_method_get_declaring_type_Delegate handle_il2cpp_method_get_declaring_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_method_get_name_Delegate(IntPtr method);
    private static il2cpp_method_get_name_Delegate handle_il2cpp_method_get_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_method_get_object_Delegate(IntPtr method, IntPtr refclass);
    private static il2cpp_method_get_object_Delegate handle_il2cpp_method_get_object;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_method_is_generic_Delegate(IntPtr method);
    private static il2cpp_method_is_generic_Delegate handle_il2cpp_method_is_generic;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_method_is_inflated_Delegate(IntPtr method);
    private static il2cpp_method_is_inflated_Delegate handle_il2cpp_method_is_inflated;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_method_is_instance_Delegate(IntPtr method);
    private static il2cpp_method_is_instance_Delegate handle_il2cpp_method_is_instance;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_method_get_param_count_Delegate(IntPtr method);
    private static il2cpp_method_get_param_count_Delegate handle_il2cpp_method_get_param_count;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_method_get_param_Delegate(IntPtr method, uint index);
    private static il2cpp_method_get_param_Delegate handle_il2cpp_method_get_param;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_method_get_class_Delegate(IntPtr method);
    private static il2cpp_method_get_class_Delegate handle_il2cpp_method_get_class;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_method_has_attribute_Delegate(IntPtr method, IntPtr attr_class);
    private static il2cpp_method_has_attribute_Delegate handle_il2cpp_method_has_attribute;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_method_get_flags_Delegate(IntPtr method, ref uint iflags);
    private static il2cpp_method_get_flags_Delegate handle_il2cpp_method_get_flags;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_method_get_token_Delegate(IntPtr method);
    private static il2cpp_method_get_token_Delegate handle_il2cpp_method_get_token;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_method_get_param_name_Delegate(IntPtr method, uint index);
    private static il2cpp_method_get_param_name_Delegate handle_il2cpp_method_get_param_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_profiler_install_Delegate(IntPtr prof, IntPtr shutdown_callback);
    private static il2cpp_profiler_install_Delegate handle_il2cpp_profiler_install;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_profiler_install_enter_leave_Delegate(IntPtr enter, IntPtr fleave);
    private static il2cpp_profiler_install_enter_leave_Delegate handle_il2cpp_profiler_install_enter_leave;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_profiler_install_allocation_Delegate(IntPtr callback);
    private static il2cpp_profiler_install_allocation_Delegate handle_il2cpp_profiler_install_allocation;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_profiler_install_gc_Delegate(IntPtr callback, IntPtr heap_resize_callback);
    private static il2cpp_profiler_install_gc_Delegate handle_il2cpp_profiler_install_gc;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_profiler_install_fileio_Delegate(IntPtr callback);
    private static il2cpp_profiler_install_fileio_Delegate handle_il2cpp_profiler_install_fileio;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_profiler_install_thread_Delegate(IntPtr start, IntPtr end);
    private static il2cpp_profiler_install_thread_Delegate handle_il2cpp_profiler_install_thread;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_property_get_flags_Delegate(IntPtr prop);
    private static il2cpp_property_get_flags_Delegate handle_il2cpp_property_get_flags;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_property_get_get_method_Delegate(IntPtr prop);
    private static il2cpp_property_get_get_method_Delegate handle_il2cpp_property_get_get_method;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_property_get_set_method_Delegate(IntPtr prop);
    private static il2cpp_property_get_set_method_Delegate handle_il2cpp_property_get_set_method;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_property_get_name_Delegate(IntPtr prop);
    private static il2cpp_property_get_name_Delegate handle_il2cpp_property_get_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_property_get_parent_Delegate(IntPtr prop);
    private static il2cpp_property_get_parent_Delegate handle_il2cpp_property_get_parent;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_object_get_class_Delegate(IntPtr obj);
    private static il2cpp_object_get_class_Delegate handle_il2cpp_object_get_class;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_object_get_size_Delegate(IntPtr obj);
    private static il2cpp_object_get_size_Delegate handle_il2cpp_object_get_size;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_object_get_virtual_method_Delegate(IntPtr obj, IntPtr method);
    private static il2cpp_object_get_virtual_method_Delegate handle_il2cpp_object_get_virtual_method;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_object_new_Delegate(IntPtr klass);
    private static il2cpp_object_new_Delegate handle_il2cpp_object_new;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_object_unbox_Delegate(IntPtr obj);
    private static il2cpp_object_unbox_Delegate handle_il2cpp_object_unbox;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_value_box_Delegate(IntPtr klass, IntPtr data);
    private static il2cpp_value_box_Delegate handle_il2cpp_value_box;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_monitor_enter_Delegate(IntPtr obj);
    private static il2cpp_monitor_enter_Delegate handle_il2cpp_monitor_enter;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_monitor_try_enter_Delegate(IntPtr obj, uint timeout);
    private static il2cpp_monitor_try_enter_Delegate handle_il2cpp_monitor_try_enter;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_monitor_exit_Delegate(IntPtr obj);
    private static il2cpp_monitor_exit_Delegate handle_il2cpp_monitor_exit;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_monitor_pulse_Delegate(IntPtr obj);
    private static il2cpp_monitor_pulse_Delegate handle_il2cpp_monitor_pulse;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_monitor_pulse_all_Delegate(IntPtr obj);
    private static il2cpp_monitor_pulse_all_Delegate handle_il2cpp_monitor_pulse_all;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_monitor_wait_Delegate(IntPtr obj);
    private static il2cpp_monitor_wait_Delegate handle_il2cpp_monitor_wait;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_monitor_try_wait_Delegate(IntPtr obj, uint timeout);
    private static il2cpp_monitor_try_wait_Delegate handle_il2cpp_monitor_try_wait;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_runtime_invoke_Delegate(IntPtr method, IntPtr obj, void** param, ref IntPtr exc);
    private static il2cpp_runtime_invoke_Delegate handle_il2cpp_runtime_invoke;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_runtime_invoke_convert_args_Delegate(IntPtr method, IntPtr obj, void** param, int paramCount, ref IntPtr exc);
    private static il2cpp_runtime_invoke_convert_args_Delegate handle_il2cpp_runtime_invoke_convert_args;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_runtime_class_init_Delegate(IntPtr klass);
    private static il2cpp_runtime_class_init_Delegate handle_il2cpp_runtime_class_init;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_runtime_object_init_Delegate(IntPtr obj);
    private static il2cpp_runtime_object_init_Delegate handle_il2cpp_runtime_object_init;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_runtime_object_init_exception_Delegate(IntPtr obj, ref IntPtr exc);
    private static il2cpp_runtime_object_init_exception_Delegate handle_il2cpp_runtime_object_init_exception;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_string_length_Delegate(IntPtr str);
    private static il2cpp_string_length_Delegate handle_il2cpp_string_length;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate char* il2cpp_string_chars_Delegate(IntPtr str);
    private static il2cpp_string_chars_Delegate handle_il2cpp_string_chars;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_string_new_Delegate(string str);
    private static il2cpp_string_new_Delegate handle_il2cpp_string_new;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_string_new_len_Delegate(string str, uint length);
    private static il2cpp_string_new_len_Delegate handle_il2cpp_string_new_len;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_string_new_utf16_Delegate(char* text, int len);
    private static il2cpp_string_new_utf16_Delegate handle_il2cpp_string_new_utf16;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_string_new_wrapper_Delegate(string str);
    private static il2cpp_string_new_wrapper_Delegate handle_il2cpp_string_new_wrapper;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_string_intern_Delegate(string str);
    private static il2cpp_string_intern_Delegate handle_il2cpp_string_intern;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_string_is_interned_Delegate(string str);
    private static il2cpp_string_is_interned_Delegate handle_il2cpp_string_is_interned;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_thread_current_Delegate();
    private static il2cpp_thread_current_Delegate handle_il2cpp_thread_current;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_thread_attach_Delegate(IntPtr domain);
    private static il2cpp_thread_attach_Delegate handle_il2cpp_thread_attach;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_thread_detach_Delegate(IntPtr thread);
    private static il2cpp_thread_detach_Delegate handle_il2cpp_thread_detach;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void** il2cpp_thread_get_all_attached_threads_Delegate(ref uint size);
    private static il2cpp_thread_get_all_attached_threads_Delegate handle_il2cpp_thread_get_all_attached_threads;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_is_vm_thread_Delegate(IntPtr thread);
    private static il2cpp_is_vm_thread_Delegate handle_il2cpp_is_vm_thread;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_current_thread_walk_frame_stack_Delegate(IntPtr func, IntPtr user_data);
    private static il2cpp_current_thread_walk_frame_stack_Delegate handle_il2cpp_current_thread_walk_frame_stack;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_thread_walk_frame_stack_Delegate(IntPtr thread, IntPtr func, IntPtr user_data);
    private static il2cpp_thread_walk_frame_stack_Delegate handle_il2cpp_thread_walk_frame_stack;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_current_thread_get_top_frame_Delegate(IntPtr frame);
    private static il2cpp_current_thread_get_top_frame_Delegate handle_il2cpp_current_thread_get_top_frame;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_thread_get_top_frame_Delegate(IntPtr thread, IntPtr frame);
    private static il2cpp_thread_get_top_frame_Delegate handle_il2cpp_thread_get_top_frame;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_current_thread_get_frame_at_Delegate(int offset, IntPtr frame);
    private static il2cpp_current_thread_get_frame_at_Delegate handle_il2cpp_current_thread_get_frame_at;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_thread_get_frame_at_Delegate(IntPtr thread, int offset, IntPtr frame);
    private static il2cpp_thread_get_frame_at_Delegate handle_il2cpp_thread_get_frame_at;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_current_thread_get_stack_depth_Delegate();
    private static il2cpp_current_thread_get_stack_depth_Delegate handle_il2cpp_current_thread_get_stack_depth;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_thread_get_stack_depth_Delegate(IntPtr thread);
    private static il2cpp_thread_get_stack_depth_Delegate handle_il2cpp_thread_get_stack_depth;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_type_get_object_Delegate(IntPtr type);
    private static il2cpp_type_get_object_Delegate handle_il2cpp_type_get_object;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int il2cpp_type_get_type_Delegate(IntPtr type);
    private static il2cpp_type_get_type_Delegate handle_il2cpp_type_get_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_type_get_class_or_element_class_Delegate(IntPtr type);
    private static il2cpp_type_get_class_or_element_class_Delegate handle_il2cpp_type_get_class_or_element_class;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_type_get_name_Delegate(IntPtr type);
    private static il2cpp_type_get_name_Delegate handle_il2cpp_type_get_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_type_is_byref_Delegate(IntPtr type);
    private static il2cpp_type_is_byref_Delegate handle_il2cpp_type_is_byref;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_type_get_attrs_Delegate(IntPtr type);
    private static il2cpp_type_get_attrs_Delegate handle_il2cpp_type_get_attrs;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_type_equals_Delegate(IntPtr type, IntPtr otherType);
    private static il2cpp_type_equals_Delegate handle_il2cpp_type_equals;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_type_get_assembly_qualified_name_Delegate(IntPtr type);
    private static il2cpp_type_get_assembly_qualified_name_Delegate handle_il2cpp_type_get_assembly_qualified_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_image_get_assembly_Delegate(IntPtr image);
    private static il2cpp_image_get_assembly_Delegate handle_il2cpp_image_get_assembly;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_image_get_name_Delegate(IntPtr image);
    private static il2cpp_image_get_name_Delegate handle_il2cpp_image_get_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_image_get_filename_Delegate(IntPtr image);
    private static il2cpp_image_get_filename_Delegate handle_il2cpp_image_get_filename;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_image_get_entry_point_Delegate(IntPtr image);
    private static il2cpp_image_get_entry_point_Delegate handle_il2cpp_image_get_entry_point;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint il2cpp_image_get_class_count_Delegate(IntPtr image);
    private static il2cpp_image_get_class_count_Delegate handle_il2cpp_image_get_class_count;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_image_get_class_Delegate(IntPtr image, uint index);
    private static il2cpp_image_get_class_Delegate handle_il2cpp_image_get_class;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_capture_memory_snapshot_Delegate();
    private static il2cpp_capture_memory_snapshot_Delegate handle_il2cpp_capture_memory_snapshot;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_free_captured_memory_snapshot_Delegate(IntPtr snapshot);
    private static il2cpp_free_captured_memory_snapshot_Delegate handle_il2cpp_free_captured_memory_snapshot;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_set_find_plugin_callback_Delegate(IntPtr method);
    private static il2cpp_set_find_plugin_callback_Delegate handle_il2cpp_set_find_plugin_callback;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_register_log_callback_Delegate(IntPtr method);
    private static il2cpp_register_log_callback_Delegate handle_il2cpp_register_log_callback;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_debugger_set_agent_options_Delegate(IntPtr options);
    private static il2cpp_debugger_set_agent_options_Delegate handle_il2cpp_debugger_set_agent_options;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_is_debugger_attached_Delegate();
    private static il2cpp_is_debugger_attached_Delegate handle_il2cpp_is_debugger_attached;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_unity_install_unitytls_interface_Delegate(void* unitytlsInterfaceStruct);
    private static il2cpp_unity_install_unitytls_interface_Delegate handle_il2cpp_unity_install_unitytls_interface;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_custom_attrs_from_class_Delegate(IntPtr klass);
    private static il2cpp_custom_attrs_from_class_Delegate handle_il2cpp_custom_attrs_from_class;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_custom_attrs_from_method_Delegate(IntPtr method);
    private static il2cpp_custom_attrs_from_method_Delegate handle_il2cpp_custom_attrs_from_method;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_custom_attrs_get_attr_Delegate(IntPtr ainfo, IntPtr attr_klass);
    private static il2cpp_custom_attrs_get_attr_Delegate handle_il2cpp_custom_attrs_get_attr;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool il2cpp_custom_attrs_has_attr_Delegate(IntPtr ainfo, IntPtr attr_klass);
    private static il2cpp_custom_attrs_has_attr_Delegate handle_il2cpp_custom_attrs_has_attr;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr il2cpp_custom_attrs_construct_Delegate(IntPtr cinfo);
    private static il2cpp_custom_attrs_construct_Delegate handle_il2cpp_custom_attrs_construct;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void il2cpp_custom_attrs_free_Delegate(IntPtr ainfo);
    private static il2cpp_custom_attrs_free_Delegate handle_il2cpp_custom_attrs_free;








    public static void il2cpp_init(IntPtr domain_name) { handle_il2cpp_init(domain_name); }
    public static void il2cpp_init_utf16(IntPtr domain_name) { handle_il2cpp_init_utf16(domain_name); }
    public static void il2cpp_shutdown() { handle_il2cpp_shutdown(); }
    public static void il2cpp_set_config_dir(IntPtr config_path) { handle_il2cpp_set_config_dir(config_path); }
    public static void il2cpp_set_data_dir(IntPtr data_path) { handle_il2cpp_set_data_dir(data_path); }
    public static void il2cpp_set_temp_dir(IntPtr temp_path) { handle_il2cpp_set_temp_dir(temp_path); }
    public static void il2cpp_set_commandline_arguments(int argc, IntPtr argv, IntPtr basedir) { handle_il2cpp_set_commandline_arguments(argc, argv, basedir); }
    public static void il2cpp_set_commandline_arguments_utf16(int argc, IntPtr argv, IntPtr basedir) { handle_il2cpp_set_commandline_arguments_utf16(argc, argv, basedir); }
    public static void il2cpp_set_config_utf16(IntPtr executablePath) { handle_il2cpp_set_config_utf16(executablePath); }
    public static void il2cpp_set_config(IntPtr executablePath) { handle_il2cpp_set_config(executablePath); }
    public static void il2cpp_set_memory_callbacks(IntPtr callbacks) { handle_il2cpp_set_memory_callbacks(callbacks); }
    public static IntPtr il2cpp_get_corlib() { return handle_il2cpp_get_corlib(); }
    public static void il2cpp_add_internal_call(IntPtr name, IntPtr method) { handle_il2cpp_add_internal_call(name, method); }
    public static IntPtr il2cpp_resolve_icall([MarshalAs(UnmanagedType.LPStr)] string name) { return handle_il2cpp_resolve_icall(name); }
    public static IntPtr il2cpp_alloc(uint size) { return handle_il2cpp_alloc(size); }
    public static void il2cpp_free(IntPtr ptr) { handle_il2cpp_free(ptr); }
    public static IntPtr il2cpp_array_class_get(IntPtr element_class, uint rank) { return handle_il2cpp_array_class_get(element_class, rank); }
    public static uint il2cpp_array_length(IntPtr array) { return handle_il2cpp_array_length(array); }
    public static uint il2cpp_array_get_byte_length(IntPtr array) { return handle_il2cpp_array_get_byte_length(array); }
    public static IntPtr il2cpp_array_new(IntPtr elementTypeInfo, ulong length) { return handle_il2cpp_array_new(elementTypeInfo, length); }
    public static IntPtr il2cpp_array_new_specific(IntPtr arrayTypeInfo, ulong length) { return handle_il2cpp_array_new_specific(arrayTypeInfo, length); }
    public static IntPtr il2cpp_array_new_full(IntPtr array_class, ref ulong lengths, ref ulong lower_bounds) { return handle_il2cpp_array_new_full(array_class,ref lengths,ref lower_bounds); }
    public static IntPtr il2cpp_bounded_array_class_get(IntPtr element_class, uint rank, [MarshalAs(UnmanagedType.I1)] bool bounded) { return handle_il2cpp_bounded_array_class_get(element_class, rank, bounded); }
    public static int il2cpp_array_element_size(IntPtr array_class) { return handle_il2cpp_array_element_size(array_class); }
    public static IntPtr il2cpp_assembly_get_image(IntPtr assembly) { return handle_il2cpp_assembly_get_image(assembly); }
    public static IntPtr il2cpp_class_enum_basetype(IntPtr klass) { return handle_il2cpp_class_enum_basetype(klass); }
    public static bool il2cpp_class_is_generic(IntPtr klass) { return handle_il2cpp_class_is_generic(klass); }
    public static bool il2cpp_class_is_inflated(IntPtr klass) { return handle_il2cpp_class_is_inflated(klass); }
    public static bool il2cpp_class_is_assignable_from(IntPtr klass, IntPtr oklass) { return handle_il2cpp_class_is_assignable_from(klass, oklass); }
    public static bool il2cpp_class_is_subclass_of(IntPtr klass, IntPtr klassc, [MarshalAs(UnmanagedType.I1)] bool check_interfaces) { return handle_il2cpp_class_is_subclass_of(klass, klassc, check_interfaces); }
    public static bool il2cpp_class_has_parent(IntPtr klass, IntPtr klassc) { return handle_il2cpp_class_has_parent(klass, klassc); }
    public static IntPtr il2cpp_class_from_il2cpp_type(IntPtr type) { return handle_il2cpp_class_from_il2cpp_type(type); }
    public static IntPtr il2cpp_class_from_name(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string namespaze, [MarshalAs(UnmanagedType.LPUTF8Str)] string name) { return handle_il2cpp_class_from_name(image, namespaze, name); }
    public static IntPtr il2cpp_class_from_system_type(IntPtr type) { return handle_il2cpp_class_from_system_type(type); }
    public static IntPtr il2cpp_class_get_element_class(IntPtr klass) { return handle_il2cpp_class_get_element_class(klass); }
    public static IntPtr il2cpp_class_get_events(IntPtr klass, ref IntPtr iter) { return handle_il2cpp_class_get_events(klass,ref iter); }
    public static IntPtr il2cpp_class_get_fields(IntPtr klass, ref IntPtr iter) { return handle_il2cpp_class_get_fields(klass,ref iter); }
    public static IntPtr il2cpp_class_get_nested_types(IntPtr klass, ref IntPtr iter) { return handle_il2cpp_class_get_nested_types(klass,ref iter); }
    public static IntPtr il2cpp_class_get_interfaces(IntPtr klass, ref IntPtr iter) { return handle_il2cpp_class_get_interfaces(klass,ref iter); }
    public static IntPtr il2cpp_class_get_properties(IntPtr klass, ref IntPtr iter) { return handle_il2cpp_class_get_properties(klass,ref iter); }
    public static IntPtr il2cpp_class_get_property_from_name(IntPtr klass, IntPtr name) { return handle_il2cpp_class_get_property_from_name(klass, name); }
    public static IntPtr il2cpp_class_get_field_from_name(IntPtr klass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name) { return handle_il2cpp_class_get_field_from_name(klass, name); }
    public static IntPtr il2cpp_class_get_methods(IntPtr klass, ref IntPtr iter) { return handle_il2cpp_class_get_methods(klass,ref iter); }
    public static IntPtr il2cpp_class_get_method_from_name(IntPtr klass, [MarshalAs(UnmanagedType.LPStr)] string name, int argsCount) { return handle_il2cpp_class_get_method_from_name(klass, name, argsCount); }
    public static IntPtr il2cpp_class_get_name(IntPtr klass) { return handle_il2cpp_class_get_name(klass); }
    public static IntPtr il2cpp_class_get_namespace(IntPtr klass) { return handle_il2cpp_class_get_namespace(klass); }
    public static IntPtr il2cpp_class_get_parent(IntPtr klass) { return handle_il2cpp_class_get_parent(klass); }
    public static IntPtr il2cpp_class_get_declaring_type(IntPtr klass) { return handle_il2cpp_class_get_declaring_type(klass); }
    public static int il2cpp_class_instance_size(IntPtr klass) { return handle_il2cpp_class_instance_size(klass); }
    public static uint il2cpp_class_num_fields(IntPtr enumKlass) { return handle_il2cpp_class_num_fields(enumKlass); }
    public static bool il2cpp_class_is_valuetype(IntPtr klass) { return handle_il2cpp_class_is_valuetype(klass); }
    public static int il2cpp_class_value_size(IntPtr klass, ref uint align) { return handle_il2cpp_class_value_size(klass,ref align); }
    public static bool il2cpp_class_is_blittable(IntPtr klass) { return handle_il2cpp_class_is_blittable(klass); }
    public static int il2cpp_class_get_flags(IntPtr klass) { return handle_il2cpp_class_get_flags(klass); }
    public static bool il2cpp_class_is_abstract(IntPtr klass) { return handle_il2cpp_class_is_abstract(klass); }
    public static bool il2cpp_class_is_interface(IntPtr klass) { return handle_il2cpp_class_is_interface(klass); }
    public static int il2cpp_class_array_element_size(IntPtr klass) { return handle_il2cpp_class_array_element_size(klass); }
    public static IntPtr il2cpp_class_from_type(IntPtr type) { return handle_il2cpp_class_from_type(type); }
    public static IntPtr il2cpp_class_get_type(IntPtr klass) { return handle_il2cpp_class_get_type(klass); }
    public static uint il2cpp_class_get_type_token(IntPtr klass) { return handle_il2cpp_class_get_type_token(klass); }
    public static bool il2cpp_class_has_attribute(IntPtr klass, IntPtr attr_class) { return handle_il2cpp_class_has_attribute(klass, attr_class); }
    public static bool il2cpp_class_has_references(IntPtr klass) { return handle_il2cpp_class_has_references(klass); }
    public static bool il2cpp_class_is_enum(IntPtr klass) { return handle_il2cpp_class_is_enum(klass); }
    public static IntPtr il2cpp_class_get_image(IntPtr klass) { return handle_il2cpp_class_get_image(klass); }
    public static IntPtr il2cpp_class_get_assemblyname(IntPtr klass) { return handle_il2cpp_class_get_assemblyname(klass); }
    public static int il2cpp_class_get_rank(IntPtr klass) { return handle_il2cpp_class_get_rank(klass); }
    public static uint il2cpp_class_get_bitmap_size(IntPtr klass) { return handle_il2cpp_class_get_bitmap_size(klass); }
    public static void il2cpp_class_get_bitmap(IntPtr klass, ref uint bitmap) { handle_il2cpp_class_get_bitmap(klass,ref bitmap); }
    public static bool il2cpp_stats_dump_to_file(IntPtr path) { return handle_il2cpp_stats_dump_to_file(path); }
    public static IntPtr il2cpp_domain_get() { return handle_il2cpp_domain_get(); }
    public static IntPtr il2cpp_domain_assembly_open(IntPtr domain, IntPtr name) { return handle_il2cpp_domain_assembly_open(domain, name); }
    public static IntPtr* il2cpp_domain_get_assemblies(IntPtr domain, ref uint size) { return handle_il2cpp_domain_get_assemblies(domain,ref size); }
    public static IntPtr il2cpp_exception_from_name_msg(IntPtr image, IntPtr name_space, IntPtr name, IntPtr msg) { return handle_il2cpp_exception_from_name_msg(image, name_space, name, msg); }
    public static IntPtr il2cpp_get_exception_argument_null(IntPtr arg) { return handle_il2cpp_get_exception_argument_null(arg); }
    public static void il2cpp_format_exception(IntPtr ex, void* message, int message_size) { handle_il2cpp_format_exception(ex, message, message_size); }
    public static void il2cpp_format_stack_trace(IntPtr ex, void* output, int output_size) { handle_il2cpp_format_stack_trace(ex, output, output_size); }
    public static void il2cpp_unhandled_exception(IntPtr ex) { handle_il2cpp_unhandled_exception(ex); }
    public static int il2cpp_field_get_flags(IntPtr field) { return handle_il2cpp_field_get_flags(field); }
    public static IntPtr il2cpp_field_get_name(IntPtr field) { return handle_il2cpp_field_get_name(field); }
    public static IntPtr il2cpp_field_get_parent(IntPtr field) { return handle_il2cpp_field_get_parent(field); }
    public static uint il2cpp_field_get_offset(IntPtr field) { return handle_il2cpp_field_get_offset(field); }
    public static IntPtr il2cpp_field_get_type(IntPtr field) { return handle_il2cpp_field_get_type(field); }
    public static void il2cpp_field_get_value(IntPtr obj, IntPtr field, void* value) { handle_il2cpp_field_get_value(obj, field, value); }
    public static IntPtr il2cpp_field_get_value_object(IntPtr field, IntPtr obj) { return handle_il2cpp_field_get_value_object(field, obj); }
    public static bool il2cpp_field_has_attribute(IntPtr field, IntPtr attr_class) { return handle_il2cpp_field_has_attribute(field, attr_class); }
    public static void il2cpp_field_set_value(IntPtr obj, IntPtr field, void* value) { handle_il2cpp_field_set_value(obj, field, value); }
    public static void il2cpp_field_static_get_value(IntPtr field, void* value) { handle_il2cpp_field_static_get_value(field, value); }
    public static void il2cpp_field_static_set_value(IntPtr field, void* value) { handle_il2cpp_field_static_set_value(field, value); }
    public static void il2cpp_field_set_value_object(IntPtr instance, IntPtr field, IntPtr value) { handle_il2cpp_field_set_value_object(instance, field, value); }
    public static void il2cpp_gc_collect(int maxGenerations) { handle_il2cpp_gc_collect(maxGenerations); }
    public static int il2cpp_gc_collect_a_little() { return handle_il2cpp_gc_collect_a_little(); }
    public static void il2cpp_gc_disable() { handle_il2cpp_gc_disable(); }
    public static void il2cpp_gc_enable() { handle_il2cpp_gc_enable(); }
    public static bool il2cpp_gc_is_disabled() { return handle_il2cpp_gc_is_disabled(); }
    public static long il2cpp_gc_get_used_size() { return handle_il2cpp_gc_get_used_size(); }
    public static long il2cpp_gc_get_heap_size() { return handle_il2cpp_gc_get_heap_size(); }
    public static void il2cpp_gc_wbarrier_set_field(IntPtr obj, IntPtr targetAddress, IntPtr gcObj) { handle_il2cpp_gc_wbarrier_set_field(obj, targetAddress, gcObj); }
    public static IntPtr il2cpp_gchandle_new(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool pinned) { return handle_il2cpp_gchandle_new(obj, pinned); }
    public static IntPtr il2cpp_gchandle_new_weakref(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool track_resurrection) { return handle_il2cpp_gchandle_new_weakref(obj, track_resurrection); }
    public static IntPtr il2cpp_gchandle_get_target(IntPtr gchandle) { return handle_il2cpp_gchandle_get_target(gchandle); }
    public static void il2cpp_gchandle_free(IntPtr gchandle) { handle_il2cpp_gchandle_free(gchandle); }
    public static IntPtr il2cpp_unity_liveness_calculation_begin(IntPtr filter, int max_object_count, IntPtr callback, IntPtr userdata, IntPtr onWorldStarted, IntPtr onWorldStopped) { return handle_il2cpp_unity_liveness_calculation_begin(filter, max_object_count, callback, userdata, onWorldStarted, onWorldStopped); }
    public static void il2cpp_unity_liveness_calculation_end(IntPtr state) { handle_il2cpp_unity_liveness_calculation_end(state); }
    public static void il2cpp_unity_liveness_calculation_from_root(IntPtr root, IntPtr state) { handle_il2cpp_unity_liveness_calculation_from_root(root, state); }
    public static void il2cpp_unity_liveness_calculation_from_statics(IntPtr state) { handle_il2cpp_unity_liveness_calculation_from_statics(state); }
    public static IntPtr il2cpp_method_get_return_type(IntPtr method) { return handle_il2cpp_method_get_return_type(method); }
    public static IntPtr il2cpp_method_get_declaring_type(IntPtr method) { return handle_il2cpp_method_get_declaring_type(method); }
    public static IntPtr il2cpp_method_get_name(IntPtr method) { return handle_il2cpp_method_get_name(method); }
    public static IntPtr il2cpp_method_get_object(IntPtr method, IntPtr refclass) { return handle_il2cpp_method_get_object(method, refclass); }
    public static bool il2cpp_method_is_generic(IntPtr method) { return handle_il2cpp_method_is_generic(method); }
    public static bool il2cpp_method_is_inflated(IntPtr method) { return handle_il2cpp_method_is_inflated(method); }
    public static bool il2cpp_method_is_instance(IntPtr method) { return handle_il2cpp_method_is_instance(method); }
    public static uint il2cpp_method_get_param_count(IntPtr method) { return handle_il2cpp_method_get_param_count(method); }
    public static IntPtr il2cpp_method_get_param(IntPtr method, uint index) { return handle_il2cpp_method_get_param(method, index); }
    public static IntPtr il2cpp_method_get_class(IntPtr method) { return handle_il2cpp_method_get_class(method); }
    public static bool il2cpp_method_has_attribute(IntPtr method, IntPtr attr_class) { return handle_il2cpp_method_has_attribute(method, attr_class); }
    public static uint il2cpp_method_get_flags(IntPtr method, ref uint iflags) { return handle_il2cpp_method_get_flags(method,ref iflags); }
    public static uint il2cpp_method_get_token(IntPtr method) { return handle_il2cpp_method_get_token(method); }
    public static IntPtr il2cpp_method_get_param_name(IntPtr method, uint index) { return handle_il2cpp_method_get_param_name(method, index); }
    public static void il2cpp_profiler_install(IntPtr prof, IntPtr shutdown_callback) { handle_il2cpp_profiler_install(prof, shutdown_callback); }
    public static void il2cpp_profiler_install_enter_leave(IntPtr enter, IntPtr fleave) { handle_il2cpp_profiler_install_enter_leave(enter, fleave); }
    public static void il2cpp_profiler_install_allocation(IntPtr callback) { handle_il2cpp_profiler_install_allocation(callback); }
    public static void il2cpp_profiler_install_gc(IntPtr callback, IntPtr heap_resize_callback) { handle_il2cpp_profiler_install_gc(callback, heap_resize_callback); }
    public static void il2cpp_profiler_install_fileio(IntPtr callback) { handle_il2cpp_profiler_install_fileio(callback); }
    public static void il2cpp_profiler_install_thread(IntPtr start, IntPtr end) { handle_il2cpp_profiler_install_thread(start, end); }
    public static uint il2cpp_property_get_flags(IntPtr prop) { return handle_il2cpp_property_get_flags(prop); }
    public static IntPtr il2cpp_property_get_get_method(IntPtr prop) { return handle_il2cpp_property_get_get_method(prop); }
    public static IntPtr il2cpp_property_get_set_method(IntPtr prop) { return handle_il2cpp_property_get_set_method(prop); }
    public static IntPtr il2cpp_property_get_name(IntPtr prop) { return handle_il2cpp_property_get_name(prop); }
    public static IntPtr il2cpp_property_get_parent(IntPtr prop) { return handle_il2cpp_property_get_parent(prop); }
    public static IntPtr il2cpp_object_get_class(IntPtr obj) { return handle_il2cpp_object_get_class(obj); }
    public static uint il2cpp_object_get_size(IntPtr obj) { return handle_il2cpp_object_get_size(obj); }
    public static IntPtr il2cpp_object_get_virtual_method(IntPtr obj, IntPtr method) { return handle_il2cpp_object_get_virtual_method(obj, method); }
    public static IntPtr il2cpp_object_new(IntPtr klass) { return handle_il2cpp_object_new(klass); }
    public static IntPtr il2cpp_object_unbox(IntPtr obj) { return handle_il2cpp_object_unbox(obj); }
    public static IntPtr il2cpp_value_box(IntPtr klass, IntPtr data) { return handle_il2cpp_value_box(klass, data); }
    public static void il2cpp_monitor_enter(IntPtr obj) { handle_il2cpp_monitor_enter(obj); }
    public static bool il2cpp_monitor_try_enter(IntPtr obj, uint timeout) { return handle_il2cpp_monitor_try_enter(obj, timeout); }
    public static void il2cpp_monitor_exit(IntPtr obj) { handle_il2cpp_monitor_exit(obj); }
    public static void il2cpp_monitor_pulse(IntPtr obj) { handle_il2cpp_monitor_pulse(obj); }
    public static void il2cpp_monitor_pulse_all(IntPtr obj) { handle_il2cpp_monitor_pulse_all(obj); }
    public static void il2cpp_monitor_wait(IntPtr obj) { handle_il2cpp_monitor_wait(obj); }
    public static bool il2cpp_monitor_try_wait(IntPtr obj, uint timeout) { return handle_il2cpp_monitor_try_wait(obj, timeout); }
    public static IntPtr il2cpp_runtime_invoke(IntPtr method, IntPtr obj, void** param, ref IntPtr exc) { return handle_il2cpp_runtime_invoke(method, obj, param,ref exc); }
    public static IntPtr il2cpp_runtime_invoke_convert_args(IntPtr method, IntPtr obj, void** param, int paramCount, ref IntPtr exc) { return handle_il2cpp_runtime_invoke_convert_args(method, obj, param, paramCount,ref exc); }
    public static void il2cpp_runtime_class_init(IntPtr klass) { handle_il2cpp_runtime_class_init(klass); }
    public static void il2cpp_runtime_object_init(IntPtr obj) { handle_il2cpp_runtime_object_init(obj); }
    public static void il2cpp_runtime_object_init_exception(IntPtr obj, ref IntPtr exc) { handle_il2cpp_runtime_object_init_exception(obj,ref exc); }
    public static int il2cpp_string_length(IntPtr str) { return handle_il2cpp_string_length(str); }
    public static char* il2cpp_string_chars(IntPtr str) { return handle_il2cpp_string_chars(str); }
    public static IntPtr il2cpp_string_new(string str) { return handle_il2cpp_string_new(str); }
    public static IntPtr il2cpp_string_new_len(string str, uint length) { return handle_il2cpp_string_new_len(str, length); }
    public static IntPtr il2cpp_string_new_utf16(char* text, int len) { return handle_il2cpp_string_new_utf16(text, len); }
    public static IntPtr il2cpp_string_new_wrapper(string str) { return handle_il2cpp_string_new_wrapper(str); }
    public static IntPtr il2cpp_string_intern(string str) { return handle_il2cpp_string_intern(str); }
    public static IntPtr il2cpp_string_is_interned(string str) { return handle_il2cpp_string_is_interned(str); }
    public static IntPtr il2cpp_thread_current() { return handle_il2cpp_thread_current(); }
    public static IntPtr il2cpp_thread_attach(IntPtr domain) { return handle_il2cpp_thread_attach(domain); }
    public static void il2cpp_thread_detach(IntPtr thread) { handle_il2cpp_thread_detach(thread); }
    public static void** il2cpp_thread_get_all_attached_threads(ref uint size) { return handle_il2cpp_thread_get_all_attached_threads(ref size); }
    public static bool il2cpp_is_vm_thread(IntPtr thread) { return handle_il2cpp_is_vm_thread(thread); }
    public static void il2cpp_current_thread_walk_frame_stack(IntPtr func, IntPtr user_data) { handle_il2cpp_current_thread_walk_frame_stack(func, user_data); }
    public static void il2cpp_thread_walk_frame_stack(IntPtr thread, IntPtr func, IntPtr user_data) { handle_il2cpp_thread_walk_frame_stack(thread, func, user_data); }
    public static bool il2cpp_current_thread_get_top_frame(IntPtr frame) { return handle_il2cpp_current_thread_get_top_frame(frame); }
    public static bool il2cpp_thread_get_top_frame(IntPtr thread, IntPtr frame) { return handle_il2cpp_thread_get_top_frame(thread, frame); }
    public static bool il2cpp_current_thread_get_frame_at(int offset, IntPtr frame) { return handle_il2cpp_current_thread_get_frame_at(offset, frame); }
    public static bool il2cpp_thread_get_frame_at(IntPtr thread, int offset, IntPtr frame) { return handle_il2cpp_thread_get_frame_at(thread, offset, frame); }
    public static int il2cpp_current_thread_get_stack_depth() { return handle_il2cpp_current_thread_get_stack_depth(); }
    public static int il2cpp_thread_get_stack_depth(IntPtr thread) { return handle_il2cpp_thread_get_stack_depth(thread); }
    public static IntPtr il2cpp_type_get_object(IntPtr type) { return handle_il2cpp_type_get_object(type); }
    public static int il2cpp_type_get_type(IntPtr type) { return handle_il2cpp_type_get_type(type); }
    public static IntPtr il2cpp_type_get_class_or_element_class(IntPtr type) { return handle_il2cpp_type_get_class_or_element_class(type); }
    public static IntPtr il2cpp_type_get_name(IntPtr type) { return handle_il2cpp_type_get_name(type); }
    public static bool il2cpp_type_is_byref(IntPtr type) { return handle_il2cpp_type_is_byref(type); }
    public static uint il2cpp_type_get_attrs(IntPtr type) { return handle_il2cpp_type_get_attrs(type); }
    public static bool il2cpp_type_equals(IntPtr type, IntPtr otherType) { return handle_il2cpp_type_equals(type, otherType); }
    public static IntPtr il2cpp_type_get_assembly_qualified_name(IntPtr type) { return handle_il2cpp_type_get_assembly_qualified_name(type); }
    public static IntPtr il2cpp_image_get_assembly(IntPtr image) { return handle_il2cpp_image_get_assembly(image); }
    public static IntPtr il2cpp_image_get_name(IntPtr image) { return handle_il2cpp_image_get_name(image); }
    public static IntPtr il2cpp_image_get_filename(IntPtr image) { return handle_il2cpp_image_get_filename(image); }
    public static IntPtr il2cpp_image_get_entry_point(IntPtr image) { return handle_il2cpp_image_get_entry_point(image); }
    public static uint il2cpp_image_get_class_count(IntPtr image) { return handle_il2cpp_image_get_class_count(image); }
    public static IntPtr il2cpp_image_get_class(IntPtr image, uint index) { return handle_il2cpp_image_get_class(image, index); }
    public static IntPtr il2cpp_capture_memory_snapshot() { return handle_il2cpp_capture_memory_snapshot(); }
    public static void il2cpp_free_captured_memory_snapshot(IntPtr snapshot) { handle_il2cpp_free_captured_memory_snapshot(snapshot); }
    public static void il2cpp_set_find_plugin_callback(IntPtr method) { handle_il2cpp_set_find_plugin_callback(method); }
    public static void il2cpp_register_log_callback(IntPtr method) { handle_il2cpp_register_log_callback(method); }
    public static void il2cpp_debugger_set_agent_options(IntPtr options) { handle_il2cpp_debugger_set_agent_options(options); }
    public static bool il2cpp_is_debugger_attached() { return handle_il2cpp_is_debugger_attached(); }
    public static void il2cpp_unity_install_unitytls_interface(void* unitytlsInterfaceStruct) { handle_il2cpp_unity_install_unitytls_interface(unitytlsInterfaceStruct); }
    public static IntPtr il2cpp_custom_attrs_from_class(IntPtr klass) { return handle_il2cpp_custom_attrs_from_class(klass); }
    public static IntPtr il2cpp_custom_attrs_from_method(IntPtr method) { return handle_il2cpp_custom_attrs_from_method(method); }
    public static IntPtr il2cpp_custom_attrs_get_attr(IntPtr ainfo, IntPtr attr_klass) { return handle_il2cpp_custom_attrs_get_attr(ainfo, attr_klass); }
    public static bool il2cpp_custom_attrs_has_attr(IntPtr ainfo, IntPtr attr_klass) { return handle_il2cpp_custom_attrs_has_attr(ainfo, attr_klass); }
    public static IntPtr il2cpp_custom_attrs_construct(IntPtr cinfo) { return handle_il2cpp_custom_attrs_construct(cinfo); }
    public static void il2cpp_custom_attrs_free(IntPtr ainfo) { handle_il2cpp_custom_attrs_free(ainfo); }









    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr il2cpp_method_get_from_reflection_Delegate(IntPtr method);
    public static il2cpp_method_get_from_reflection_Delegate _il2cpp_method_get_from_reflection;




    public static Dictionary<string, string> Fake2TrueName = new Dictionary<string, string>();


    public static void CreateDictionaryFromFiles(string path1, string path2)
    {
        // 读取文件1的数据
        (int cnt, List<string> contentList1) = ReadFileWithCount(path1);

        // 读取文件2的数据
        (int cnt2, List<string> contentList2) = ReadFileWithCount(path2);

        // 验证两个文件的计数是否匹配
        if (cnt != cnt2)
        {
            throw new InvalidDataException($"File count mismatch: {cnt} in {Path.GetFileName(path1)} vs {cnt2} in {Path.GetFileName(path2)}");
        }


        // 填充字典（使用索引位置对应）
        for (int i = 0; i < cnt; i++)
        {
            string key = contentList1[i];
            string value = contentList2[i];

            // 检查重复键
            if (Fake2TrueName.ContainsKey(key))
            {
                throw new InvalidDataException($"Duplicate key detected: '{key}' in file {Path.GetFileName(path1)}");
            }

            Fake2TrueName.Add(key, value);
        }
    }

    private static (int count, List<string> contents) ReadFileWithCount(string filePath)
    {
        // 确保文件存在
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        List<string> contents = new List<string>();
        int count = 0;
        bool countRead = false;

        // 读取所有行
        string[] allLines = File.ReadAllLines(filePath);

        // 处理空文件
        if (allLines.Length == 0)
        {
            throw new InvalidDataException($"Empty file: {filePath}");
        }

        // 处理第一行（计数行）
        if (!int.TryParse(allLines[0], out count))
        {
            throw new InvalidDataException($"Invalid count format in {filePath}");
        }
        countRead = true;

        // 验证实际行数
        int expectedLines = count + 1;
        if (allLines.Length != expectedLines)
        {
            throw new InvalidDataException($"Line count mismatch in {filePath}. Expected {expectedLines}, found {allLines.Length}");
        }

        // 提取内容行（跳过第一行）
        for (int i = 1; i < allLines.Length; i++)
        {
            contents.Add(allLines[i]);
        }

        return (count, contents);
    }

    public static void InitIL2CPPExports()
    {
        Console.WriteLine("Hello, World!__Dumper running");
        //File.WriteAllText("asqw.txt", "");
        if (!NativeLibrary.TryLoad("GameAssembly", out GameAssemblyHandle))
        {
            Console.WriteLine("Failed to load GameAssembly.");
            Logger.Instance.LogError("Failed to load GameAssembly.");
        }
        CreateDictionaryFromFiles("savedSecretNameNoEnc.txt", "savedSecretName.txt");





        handle_il2cpp_init = Marshal.GetDelegateForFunctionPointer<il2cpp_init_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_init"]));
        handle_il2cpp_init_utf16 = Marshal.GetDelegateForFunctionPointer<il2cpp_init_utf16_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_init_utf16"]));
        handle_il2cpp_shutdown = Marshal.GetDelegateForFunctionPointer<il2cpp_shutdown_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_shutdown"]));
        handle_il2cpp_set_config_dir = Marshal.GetDelegateForFunctionPointer<il2cpp_set_config_dir_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_set_config_dir"]));
        handle_il2cpp_set_data_dir = Marshal.GetDelegateForFunctionPointer<il2cpp_set_data_dir_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_set_data_dir"]));
        handle_il2cpp_set_temp_dir = Marshal.GetDelegateForFunctionPointer<il2cpp_set_temp_dir_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_set_temp_dir"]));
        handle_il2cpp_set_commandline_arguments = Marshal.GetDelegateForFunctionPointer<il2cpp_set_commandline_arguments_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_set_commandline_arguments"]));
        handle_il2cpp_set_commandline_arguments_utf16 = Marshal.GetDelegateForFunctionPointer<il2cpp_set_commandline_arguments_utf16_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_set_commandline_arguments_utf16"]));
        handle_il2cpp_set_config_utf16 = Marshal.GetDelegateForFunctionPointer<il2cpp_set_config_utf16_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_set_config_utf16"]));
        handle_il2cpp_set_config = Marshal.GetDelegateForFunctionPointer<il2cpp_set_config_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_set_config"]));
        handle_il2cpp_set_memory_callbacks = Marshal.GetDelegateForFunctionPointer<il2cpp_set_memory_callbacks_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_set_memory_callbacks"]));
        handle_il2cpp_get_corlib = Marshal.GetDelegateForFunctionPointer<il2cpp_get_corlib_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_get_corlib"]));
        handle_il2cpp_add_internal_call = Marshal.GetDelegateForFunctionPointer<il2cpp_add_internal_call_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_add_internal_call"]));
        handle_il2cpp_resolve_icall = Marshal.GetDelegateForFunctionPointer<il2cpp_resolve_icall_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_resolve_icall"]));
        handle_il2cpp_alloc = Marshal.GetDelegateForFunctionPointer<il2cpp_alloc_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_alloc"]));
        handle_il2cpp_free = Marshal.GetDelegateForFunctionPointer<il2cpp_free_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_free"]));
        handle_il2cpp_array_class_get = Marshal.GetDelegateForFunctionPointer<il2cpp_array_class_get_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_array_class_get"]));
        handle_il2cpp_array_length = Marshal.GetDelegateForFunctionPointer<il2cpp_array_length_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_array_length"]));
        handle_il2cpp_array_get_byte_length = Marshal.GetDelegateForFunctionPointer<il2cpp_array_get_byte_length_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_array_get_byte_length"]));
        handle_il2cpp_array_new = Marshal.GetDelegateForFunctionPointer<il2cpp_array_new_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_array_new"]));
        handle_il2cpp_array_new_specific = Marshal.GetDelegateForFunctionPointer<il2cpp_array_new_specific_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_array_new_specific"]));
        handle_il2cpp_array_new_full = Marshal.GetDelegateForFunctionPointer<il2cpp_array_new_full_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_array_new_full"]));
        handle_il2cpp_bounded_array_class_get = Marshal.GetDelegateForFunctionPointer<il2cpp_bounded_array_class_get_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_bounded_array_class_get"]));
        handle_il2cpp_array_element_size = Marshal.GetDelegateForFunctionPointer<il2cpp_array_element_size_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_array_element_size"]));
        handle_il2cpp_assembly_get_image = Marshal.GetDelegateForFunctionPointer<il2cpp_assembly_get_image_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_assembly_get_image"]));
        handle_il2cpp_class_enum_basetype = Marshal.GetDelegateForFunctionPointer<il2cpp_class_enum_basetype_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_enum_basetype"]));
        handle_il2cpp_class_is_generic = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_generic_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_is_generic"]));
        handle_il2cpp_class_is_inflated = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_inflated_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_is_inflated"]));
        handle_il2cpp_class_is_assignable_from = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_assignable_from_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_is_assignable_from"]));
        handle_il2cpp_class_is_subclass_of = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_subclass_of_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_is_subclass_of"]));
        handle_il2cpp_class_has_parent = Marshal.GetDelegateForFunctionPointer<il2cpp_class_has_parent_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_has_parent"]));
        handle_il2cpp_class_from_il2cpp_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_from_il2cpp_type_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_from_il2cpp_type"]));
        handle_il2cpp_class_from_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_from_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_from_name"]));
        handle_il2cpp_class_from_system_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_from_system_type_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_from_system_type"]));
        handle_il2cpp_class_get_element_class = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_element_class_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_element_class"]));
        handle_il2cpp_class_get_events = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_events_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_events"]));
        handle_il2cpp_class_get_fields = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_fields_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_fields"]));
        handle_il2cpp_class_get_nested_types = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_nested_types_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_nested_types"]));
        handle_il2cpp_class_get_interfaces = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_interfaces_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_interfaces"]));
        handle_il2cpp_class_get_properties = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_properties_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_properties"]));
        handle_il2cpp_class_get_property_from_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_property_from_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_property_from_name"]));
        handle_il2cpp_class_get_field_from_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_field_from_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_field_from_name"]));
        handle_il2cpp_class_get_methods = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_methods_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_methods"]));
        handle_il2cpp_class_get_method_from_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_method_from_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_method_from_name"]));
        handle_il2cpp_class_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_name"]));
        handle_il2cpp_class_get_namespace = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_namespace_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_namespace"]));
        handle_il2cpp_class_get_parent = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_parent_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_parent"]));
        handle_il2cpp_class_get_declaring_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_declaring_type_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_declaring_type"]));
        handle_il2cpp_class_instance_size = Marshal.GetDelegateForFunctionPointer<il2cpp_class_instance_size_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_instance_size"]));
        handle_il2cpp_class_num_fields = Marshal.GetDelegateForFunctionPointer<il2cpp_class_num_fields_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_num_fields"]));
        handle_il2cpp_class_is_valuetype = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_valuetype_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_is_valuetype"]));
        handle_il2cpp_class_value_size = Marshal.GetDelegateForFunctionPointer<il2cpp_class_value_size_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_value_size"]));
        handle_il2cpp_class_is_blittable = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_blittable_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_is_blittable"]));
        handle_il2cpp_class_get_flags = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_flags_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_flags"]));
        handle_il2cpp_class_is_abstract = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_abstract_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_is_abstract"]));
        handle_il2cpp_class_is_interface = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_interface_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_is_interface"]));
        handle_il2cpp_class_array_element_size = Marshal.GetDelegateForFunctionPointer<il2cpp_class_array_element_size_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_array_element_size"]));
        handle_il2cpp_class_from_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_from_type_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_from_type"]));
        handle_il2cpp_class_get_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_type_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_type"]));
        handle_il2cpp_class_get_type_token = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_type_token_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_type_token"]));
        handle_il2cpp_class_has_attribute = Marshal.GetDelegateForFunctionPointer<il2cpp_class_has_attribute_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_has_attribute"]));
        handle_il2cpp_class_has_references = Marshal.GetDelegateForFunctionPointer<il2cpp_class_has_references_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_has_references"]));
        handle_il2cpp_class_is_enum = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_enum_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_is_enum"]));
        handle_il2cpp_class_get_image = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_image_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_image"]));
        handle_il2cpp_class_get_assemblyname = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_assemblyname_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_assemblyname"]));
        handle_il2cpp_class_get_rank = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_rank_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_rank"]));
        handle_il2cpp_class_get_bitmap_size = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_bitmap_size_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_bitmap_size"]));
        handle_il2cpp_class_get_bitmap = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_bitmap_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_class_get_bitmap"]));
        handle_il2cpp_stats_dump_to_file = Marshal.GetDelegateForFunctionPointer<il2cpp_stats_dump_to_file_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_stats_dump_to_file"]));
        handle_il2cpp_domain_get = Marshal.GetDelegateForFunctionPointer<il2cpp_domain_get_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_domain_get"]));
        handle_il2cpp_domain_assembly_open = Marshal.GetDelegateForFunctionPointer<il2cpp_domain_assembly_open_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_domain_assembly_open"]));
        handle_il2cpp_domain_get_assemblies = Marshal.GetDelegateForFunctionPointer<il2cpp_domain_get_assemblies_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_domain_get_assemblies"]));
        handle_il2cpp_exception_from_name_msg = Marshal.GetDelegateForFunctionPointer<il2cpp_exception_from_name_msg_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_exception_from_name_msg"]));
        handle_il2cpp_get_exception_argument_null = Marshal.GetDelegateForFunctionPointer<il2cpp_get_exception_argument_null_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_get_exception_argument_null"]));
        handle_il2cpp_format_exception = Marshal.GetDelegateForFunctionPointer<il2cpp_format_exception_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_format_exception"]));
        handle_il2cpp_format_stack_trace = Marshal.GetDelegateForFunctionPointer<il2cpp_format_stack_trace_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_format_stack_trace"]));
        handle_il2cpp_unhandled_exception = Marshal.GetDelegateForFunctionPointer<il2cpp_unhandled_exception_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_unhandled_exception"]));
        handle_il2cpp_field_get_flags = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_flags_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_get_flags"]));
        handle_il2cpp_field_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_get_name"]));
        handle_il2cpp_field_get_parent = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_parent_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_get_parent"]));
        handle_il2cpp_field_get_offset = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_offset_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_get_offset"]));
        handle_il2cpp_field_get_type = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_type_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_get_type"]));
        handle_il2cpp_field_get_value = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_value_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_get_value"]));
        handle_il2cpp_field_get_value_object = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_value_object_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_get_value_object"]));
        handle_il2cpp_field_has_attribute = Marshal.GetDelegateForFunctionPointer<il2cpp_field_has_attribute_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_has_attribute"]));
        handle_il2cpp_field_set_value = Marshal.GetDelegateForFunctionPointer<il2cpp_field_set_value_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_set_value"]));
        handle_il2cpp_field_static_get_value = Marshal.GetDelegateForFunctionPointer<il2cpp_field_static_get_value_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_static_get_value"]));
        handle_il2cpp_field_static_set_value = Marshal.GetDelegateForFunctionPointer<il2cpp_field_static_set_value_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_static_set_value"]));
        handle_il2cpp_field_set_value_object = Marshal.GetDelegateForFunctionPointer<il2cpp_field_set_value_object_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_field_set_value_object"]));
        handle_il2cpp_gc_collect = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_collect_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gc_collect"]));
        handle_il2cpp_gc_collect_a_little = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_collect_a_little_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gc_collect_a_little"]));
        handle_il2cpp_gc_disable = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_disable_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gc_disable"]));
        handle_il2cpp_gc_enable = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_enable_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gc_enable"]));
        handle_il2cpp_gc_is_disabled = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_is_disabled_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gc_is_disabled"]));
        handle_il2cpp_gc_get_used_size = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_get_used_size_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gc_get_used_size"]));
        handle_il2cpp_gc_get_heap_size = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_get_heap_size_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gc_get_heap_size"]));
        handle_il2cpp_gc_wbarrier_set_field = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_wbarrier_set_field_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gc_wbarrier_set_field"]));
        handle_il2cpp_gchandle_new = Marshal.GetDelegateForFunctionPointer<il2cpp_gchandle_new_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gchandle_new"]));
        handle_il2cpp_gchandle_new_weakref = Marshal.GetDelegateForFunctionPointer<il2cpp_gchandle_new_weakref_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gchandle_new_weakref"]));
        handle_il2cpp_gchandle_get_target = Marshal.GetDelegateForFunctionPointer<il2cpp_gchandle_get_target_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gchandle_get_target"]));
        handle_il2cpp_gchandle_free = Marshal.GetDelegateForFunctionPointer<il2cpp_gchandle_free_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_gchandle_free"]));
        //handle_il2cpp_unity_liveness_calculation_begin = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_liveness_calculation_begin_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_unity_liveness_calculation_begin"]));
        //handle_il2cpp_unity_liveness_calculation_end = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_liveness_calculation_end_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_unity_liveness_calculation_end"]));
        handle_il2cpp_unity_liveness_calculation_from_root = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_liveness_calculation_from_root_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_unity_liveness_calculation_from_root"]));
        handle_il2cpp_unity_liveness_calculation_from_statics = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_liveness_calculation_from_statics_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_unity_liveness_calculation_from_statics"]));
        handle_il2cpp_method_get_return_type = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_return_type_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_return_type"]));
        handle_il2cpp_method_get_declaring_type = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_declaring_type_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_declaring_type"]));
        handle_il2cpp_method_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_name"]));
        handle_il2cpp_method_get_object = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_object_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_object"]));
        handle_il2cpp_method_is_generic = Marshal.GetDelegateForFunctionPointer<il2cpp_method_is_generic_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_is_generic"]));
        handle_il2cpp_method_is_inflated = Marshal.GetDelegateForFunctionPointer<il2cpp_method_is_inflated_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_is_inflated"]));
        handle_il2cpp_method_is_instance = Marshal.GetDelegateForFunctionPointer<il2cpp_method_is_instance_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_is_instance"]));
        handle_il2cpp_method_get_param_count = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_param_count_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_param_count"]));
        handle_il2cpp_method_get_param = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_param_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_param"]));
        handle_il2cpp_method_get_class = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_class_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_class"]));
        handle_il2cpp_method_has_attribute = Marshal.GetDelegateForFunctionPointer<il2cpp_method_has_attribute_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_has_attribute"]));
        handle_il2cpp_method_get_flags = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_flags_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_flags"]));
        handle_il2cpp_method_get_token = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_token_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_token"]));
        handle_il2cpp_method_get_param_name = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_param_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_param_name"]));
        //handle_il2cpp_profiler_install = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_profiler_install"]));
        //handle_il2cpp_profiler_install_enter_leave = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_enter_leave_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_profiler_install_enter_leave"]));
        //handle_il2cpp_profiler_install_allocation = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_allocation_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_profiler_install_allocation"]));
        //handle_il2cpp_profiler_install_gc = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_gc_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_profiler_install_gc"]));
        //handle_il2cpp_profiler_install_fileio = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_fileio_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_profiler_install_fileio"]));
        //handle_il2cpp_profiler_install_thread = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_thread_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_profiler_install_thread"]));
        handle_il2cpp_property_get_flags = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_flags_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_property_get_flags"]));
        handle_il2cpp_property_get_get_method = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_get_method_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_property_get_get_method"]));
        handle_il2cpp_property_get_set_method = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_set_method_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_property_get_set_method"]));
        handle_il2cpp_property_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_property_get_name"]));
        handle_il2cpp_property_get_parent = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_parent_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_property_get_parent"]));
        handle_il2cpp_object_get_class = Marshal.GetDelegateForFunctionPointer<il2cpp_object_get_class_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_object_get_class"]));
        handle_il2cpp_object_get_size = Marshal.GetDelegateForFunctionPointer<il2cpp_object_get_size_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_object_get_size"]));
        handle_il2cpp_object_get_virtual_method = Marshal.GetDelegateForFunctionPointer<il2cpp_object_get_virtual_method_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_object_get_virtual_method"]));
        handle_il2cpp_object_new = Marshal.GetDelegateForFunctionPointer<il2cpp_object_new_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_object_new"]));
        handle_il2cpp_object_unbox = Marshal.GetDelegateForFunctionPointer<il2cpp_object_unbox_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_object_unbox"]));
        handle_il2cpp_value_box = Marshal.GetDelegateForFunctionPointer<il2cpp_value_box_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_value_box"]));
        handle_il2cpp_monitor_enter = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_enter_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_monitor_enter"]));
        handle_il2cpp_monitor_try_enter = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_try_enter_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_monitor_try_enter"]));
        handle_il2cpp_monitor_exit = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_exit_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_monitor_exit"]));
        handle_il2cpp_monitor_pulse = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_pulse_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_monitor_pulse"]));
        handle_il2cpp_monitor_pulse_all = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_pulse_all_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_monitor_pulse_all"]));
        handle_il2cpp_monitor_wait = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_wait_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_monitor_wait"]));
        handle_il2cpp_monitor_try_wait = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_try_wait_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_monitor_try_wait"]));
        handle_il2cpp_runtime_invoke = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_invoke_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_runtime_invoke"]));
        handle_il2cpp_runtime_invoke_convert_args = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_invoke_convert_args_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_runtime_invoke_convert_args"]));
        handle_il2cpp_runtime_class_init = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_class_init_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_runtime_class_init"]));
        handle_il2cpp_runtime_object_init = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_object_init_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_runtime_object_init"]));
        handle_il2cpp_runtime_object_init_exception = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_object_init_exception_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_runtime_object_init_exception"]));
        handle_il2cpp_string_length = Marshal.GetDelegateForFunctionPointer<il2cpp_string_length_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_string_length"]));
        handle_il2cpp_string_chars = Marshal.GetDelegateForFunctionPointer<il2cpp_string_chars_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_string_chars"]));
        handle_il2cpp_string_new = Marshal.GetDelegateForFunctionPointer<il2cpp_string_new_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_string_new"]));
        handle_il2cpp_string_new_len = Marshal.GetDelegateForFunctionPointer<il2cpp_string_new_len_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_string_new_len"]));
        handle_il2cpp_string_new_utf16 = Marshal.GetDelegateForFunctionPointer<il2cpp_string_new_utf16_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_string_new_utf16"]));
        handle_il2cpp_string_new_wrapper = Marshal.GetDelegateForFunctionPointer<il2cpp_string_new_wrapper_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_string_new_wrapper"]));
        handle_il2cpp_string_intern = Marshal.GetDelegateForFunctionPointer<il2cpp_string_intern_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_string_intern"]));
        handle_il2cpp_string_is_interned = Marshal.GetDelegateForFunctionPointer<il2cpp_string_is_interned_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_string_is_interned"]));
        handle_il2cpp_thread_current = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_current_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_thread_current"]));
        handle_il2cpp_thread_attach = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_attach_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_thread_attach"]));
        handle_il2cpp_thread_detach = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_detach_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_thread_detach"]));
        handle_il2cpp_thread_get_all_attached_threads = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_get_all_attached_threads_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_thread_get_all_attached_threads"]));
        handle_il2cpp_is_vm_thread = Marshal.GetDelegateForFunctionPointer<il2cpp_is_vm_thread_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_is_vm_thread"]));
        handle_il2cpp_current_thread_walk_frame_stack = Marshal.GetDelegateForFunctionPointer<il2cpp_current_thread_walk_frame_stack_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_current_thread_walk_frame_stack"]));
        handle_il2cpp_thread_walk_frame_stack = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_walk_frame_stack_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_thread_walk_frame_stack"]));
        handle_il2cpp_current_thread_get_top_frame = Marshal.GetDelegateForFunctionPointer<il2cpp_current_thread_get_top_frame_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_current_thread_get_top_frame"]));
        handle_il2cpp_thread_get_top_frame = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_get_top_frame_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_thread_get_top_frame"]));
        handle_il2cpp_current_thread_get_frame_at = Marshal.GetDelegateForFunctionPointer<il2cpp_current_thread_get_frame_at_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_current_thread_get_frame_at"]));
        handle_il2cpp_thread_get_frame_at = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_get_frame_at_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_thread_get_frame_at"]));
        handle_il2cpp_current_thread_get_stack_depth = Marshal.GetDelegateForFunctionPointer<il2cpp_current_thread_get_stack_depth_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_current_thread_get_stack_depth"]));
        handle_il2cpp_thread_get_stack_depth = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_get_stack_depth_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_thread_get_stack_depth"]));
        handle_il2cpp_type_get_object = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_object_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_type_get_object"]));
        handle_il2cpp_type_get_type = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_type_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_type_get_type"]));
        handle_il2cpp_type_get_class_or_element_class = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_class_or_element_class_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_type_get_class_or_element_class"]));
        handle_il2cpp_type_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_type_get_name"]));
        handle_il2cpp_type_is_byref = Marshal.GetDelegateForFunctionPointer<il2cpp_type_is_byref_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_type_is_byref"]));
        handle_il2cpp_type_get_attrs = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_attrs_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_type_get_attrs"]));
        handle_il2cpp_type_equals = Marshal.GetDelegateForFunctionPointer<il2cpp_type_equals_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_type_equals"]));
        handle_il2cpp_type_get_assembly_qualified_name = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_assembly_qualified_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_type_get_assembly_qualified_name"]));
        handle_il2cpp_image_get_assembly = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_assembly_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_image_get_assembly"]));
        handle_il2cpp_image_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_name_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_image_get_name"]));
        handle_il2cpp_image_get_filename = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_filename_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_image_get_filename"]));
        handle_il2cpp_image_get_entry_point = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_entry_point_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_image_get_entry_point"]));
        handle_il2cpp_image_get_class_count = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_class_count_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_image_get_class_count"]));
        handle_il2cpp_image_get_class = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_class_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_image_get_class"]));
        handle_il2cpp_capture_memory_snapshot = Marshal.GetDelegateForFunctionPointer<il2cpp_capture_memory_snapshot_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_capture_memory_snapshot"]));
        handle_il2cpp_free_captured_memory_snapshot = Marshal.GetDelegateForFunctionPointer<il2cpp_free_captured_memory_snapshot_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_free_captured_memory_snapshot"]));
        handle_il2cpp_set_find_plugin_callback = Marshal.GetDelegateForFunctionPointer<il2cpp_set_find_plugin_callback_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_set_find_plugin_callback"]));
        handle_il2cpp_register_log_callback = Marshal.GetDelegateForFunctionPointer<il2cpp_register_log_callback_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_register_log_callback"]));
        handle_il2cpp_debugger_set_agent_options = Marshal.GetDelegateForFunctionPointer<il2cpp_debugger_set_agent_options_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_debugger_set_agent_options"]));
        handle_il2cpp_is_debugger_attached = Marshal.GetDelegateForFunctionPointer<il2cpp_is_debugger_attached_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_is_debugger_attached"]));
        handle_il2cpp_unity_install_unitytls_interface = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_install_unitytls_interface_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_unity_install_unitytls_interface"]));
        handle_il2cpp_custom_attrs_from_class = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_from_class_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_custom_attrs_from_class"]));
        handle_il2cpp_custom_attrs_from_method = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_from_method_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_custom_attrs_from_method"]));
        handle_il2cpp_custom_attrs_get_attr = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_get_attr_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_custom_attrs_get_attr"]));
        handle_il2cpp_custom_attrs_has_attr = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_has_attr_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_custom_attrs_has_attr"]));
        handle_il2cpp_custom_attrs_construct = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_construct_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_custom_attrs_construct"]));
        handle_il2cpp_custom_attrs_free = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_free_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_custom_attrs_free"]));






        _il2cpp_method_get_from_reflection = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_from_reflection_Delegate>(NativeLibrary.GetExport(GameAssemblyHandle, Fake2TrueName["il2cpp_method_get_from_reflection"]));








    }
}
