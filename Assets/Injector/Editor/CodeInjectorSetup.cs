using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;
using MethodBody = Mono.Cecil.Cil.MethodBody;
using OpCodes = Mono.Cecil.Cil.OpCodes;

public class CodeInjectorSetup
{
    private readonly List<string> enginePaths = new List<string>();
    private readonly List<string> assemblys = new List<string>();
    public string BuildTarget;
    public string OutputDirectory;

    public void AddAssemblySearchDirectory(string enginePath)
    {
        enginePaths.Add(enginePath);
    }

    public void AddAssembly(string scriptPath)
    {
        assemblys.Add(scriptPath);
    }

    public void Run()
    {
        foreach (var path in assemblys)
        {
            var assembly = ReadAssembly(path);
            var result = DoInjector(assembly);
            if (result)
            {
                SaveAssembly(path, assembly);
            }
        }
    }

    private AssemblyDefinition ReadAssembly(string path)
    {
        Debug.Log(string.Format("ReadAssembly: {0}", path));
        var assemblyResolver = new DefaultAssemblyResolver();
        foreach (var enginePath in enginePaths)
        {
            Debug.Log(string.Format("AddSearchDirectory: {0}", enginePath));
            assemblyResolver.AddSearchDirectory(enginePath);
        }
        var readerParameters = new ReaderParameters
        {
            AssemblyResolver = assemblyResolver,
            ReadingMode = ReadingMode.Immediate,
            ReadSymbols = true
        };
        var assembly = AssemblyDefinition.ReadAssembly(path, readerParameters);
        return assembly;
    }

    private void SaveAssembly(string path, AssemblyDefinition assembly)
    {
        var outPath = Path.Combine(OutputDirectory, Path.GetFileName(path));
        Debug.Log(string.Format("WriteAssembly: {0}", outPath));

        var writerParameters = new WriterParameters { WriteSymbols = true };
        assembly.Write(outPath, writerParameters);
    }

    private static bool DoInjector(AssemblyDefinition assembly)
    {
        var modified = false;
        foreach (var type in assembly.MainModule.Types)
        {
            if (type.HasCustomAttribute<LuaInjectorAttribute>())
            {
                foreach (var method in type.Methods)
                {
                    if (method.HasCustomAttribute<LuaInjectorIgnoreAttribute>()) continue;

                    DoInjectMethod(assembly, method, type);
                    modified = true;
                }
            }
            else
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasCustomAttribute<LuaInjectorAttribute>()) continue;

                    DoInjectMethod(assembly, method, type);
                    modified = true;
                }
            }
        }
        return modified;
    }

    private static void DoInjectMethod(AssemblyDefinition assembly, MethodDefinition method, TypeDefinition type)
    {
        if (method.Name.Equals(".ctor") || !method.HasBody) return;

        var firstIns = method.Body.Instructions.First();
        var worker = method.Body.GetILProcessor();

        // bool result = LuaPatch.HasPatch(type.Name)
        var hasPatchRef = assembly.MainModule.Import(typeof(LuaPatch).GetMethod("HasPatch"));
        var current = InsertBefore(worker, firstIns, worker.Create(OpCodes.Ldstr, type.Name));
        current = InsertAfter(worker, current, worker.Create(OpCodes.Ldstr, method.Name));
        current = InsertAfter(worker, current, worker.Create(OpCodes.Call, hasPatchRef));

        // if(result == false) jump to the under code
        current = InsertAfter(worker, current, worker.Create(OpCodes.Brfalse, firstIns));

        // else LuaPatch.CallPatch(type.Name, method.Name, args)
        var callPatchRef = assembly.MainModule.Import(typeof(LuaPatch).GetMethod("CallPatch"));
        current = InsertAfter(worker, current, worker.Create(OpCodes.Ldstr, type.Name));
        current = InsertAfter(worker, current, worker.Create(OpCodes.Ldstr, method.Name));
        var paramsCount = method.Parameters.Count;
        // ���� args���� object[] ����
        current = InsertAfter(worker, current, worker.Create(OpCodes.Ldc_I4, paramsCount));
        current = InsertAfter(worker, current, worker.Create(OpCodes.Newarr, assembly.MainModule.Import(typeof(object))));
        for (int index = 0; index < paramsCount; index++)
        {
            var argIndex = method.IsStatic ? index : index + 1;
            // ѹ�����
            current = InsertAfter(worker, current, worker.Create(OpCodes.Dup));
            current = InsertAfter(worker, current, worker.Create(OpCodes.Ldc_I4, index));
            current = InsertAfter(worker, current, worker.Create(OpCodes.Ldarg, argIndex));
            current = InsertAfter(worker, current, worker.Create(OpCodes.Stelem_Ref));
        }
        current = InsertAfter(worker, current, worker.Create(OpCodes.Call, callPatchRef));
        // �����з���ֵʱ
        if (!method.ReturnType.FullName.Equals("System.Void"))
        {
            current = InsertAfter(worker, current, worker.Create(OpCodes.Unbox_Any, method.ReturnType));
        }
        // return
        InsertAfter(worker, current, worker.Create(OpCodes.Ret));

        // ���¼������λ��ƫ��ֵ
        ComputeOffsets(method.Body);
    }
    /// <summary>
    /// ���ǰ����Instruction, �����ص�ǰ���
    /// </summary>
    private static Instruction InsertBefore(ILProcessor worker, Instruction target, Instruction instruction)
    {
        worker.InsertBefore(target, instruction);
        return instruction;
    }

    /// <summary>
    /// �������Instruction, �����ص�ǰ���
    /// </summary>
    private static Instruction InsertAfter(ILProcessor worker, Instruction target, Instruction instruction)
    {
        worker.InsertAfter(target, instruction);
        return instruction;
    }

    private static void ComputeOffsets(MethodBody body)
    {
        var offset = 0;
        foreach (var instruction in body.Instructions)
        {
            instruction.Offset = offset;
            offset += instruction.GetSize();
        }
    }
}