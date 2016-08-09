﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;

namespace SiliconStudio.AssemblyProcessor
{
    internal class DispatcherProcessor : IAssemblyDefinitionProcessor
    {
        private readonly HashSet<MethodDefinition> factoryMethods = new HashSet<MethodDefinition>();
        private readonly Dictionary<TypeDefinition, ClosureInfo> closures = new Dictionary<TypeDefinition, ClosureInfo>();

        private AssemblyDefinition mscorlibAssembly;
        private AssemblyDefinition siliconStudioCoreAssembly;

        private TypeReference pooledClosureType;

        private TypeDefinition funcType;
        private MethodReference funcConstructor;

        private TypeDefinition poolType;
        private MethodReference poolConstructor;

        private TypeDefinition interlockedType;
        private MethodReference interlockedIncrementMethod;
        private MethodReference interlockedDecrementMethod;

        private class ClosureInfo
        {
            public MethodDefinition FactoryMethod;

            public MethodDefinition AddReferenceMethod;

            public MethodDefinition ReleaseMethod;

            public FieldDefinition PoolField;
        }

        public bool Process(AssemblyProcessorContext context)
        {
            mscorlibAssembly = CecilExtensions.FindCorlibAssembly(context.Assembly);

            siliconStudioCoreAssembly = context.Assembly.Name.Name == "SiliconStudio.Core" ? context.Assembly :
                context.AssemblyResolver.Resolve("SiliconStudio.Core");

            pooledClosureType = context.Assembly.MainModule.ImportReference(siliconStudioCoreAssembly.MainModule.GetType("SiliconStudio.Core.Threading.IPooledClosure"));

            // Func type and it's contructor
            funcType = mscorlibAssembly.MainModule.GetTypeResolved("System.Func`1");
            funcConstructor = context.Assembly.MainModule.ImportReference(funcType.Methods.FirstOrDefault(x => x.Name == ".ctor"));

            // Pool type and it's constructor
            poolType = siliconStudioCoreAssembly.MainModule.GetType("SiliconStudio.Core.Threading.ConcurrentPool`1");
            poolConstructor = context.Assembly.MainModule.ImportReference(poolType.Methods.FirstOrDefault(x => x.Name == ".ctor"));

            // Interlocked
            interlockedType = mscorlibAssembly.MainModule.GetTypeResolved("System.Threading.Interlocked");
            interlockedIncrementMethod = context.Assembly.MainModule.ImportReference(interlockedType.Methods.FirstOrDefault(x => x.Name == "Increment" && x.ReturnType.FullName == "System.Int32"));
            interlockedDecrementMethod = context.Assembly.MainModule.ImportReference(interlockedType.Methods.FirstOrDefault(x => x.Name == "Decrement" && x.ReturnType.FullName == "System.Int32"));

            bool changed = false;

            foreach (var type in context.Assembly.EnumerateTypes().ToArray())
            {
                foreach (var method in type.Methods.ToArray())
                {
                    if (method.Body == null)
                        continue;

                    foreach (var instruction in method.Body.Instructions.ToArray())
                    {
                        if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                        {
                            var calledMethod = ((MethodReference)instruction.Operand).Resolve();
                            if (calledMethod == null)
                                continue;

                            for (int parameterIndex = 0; parameterIndex < calledMethod.Parameters.Count; parameterIndex++)
                            {
                                var parameter = calledMethod.Parameters[parameterIndex];

                                // Parameter must be decorated with PooledAttribute
                                if (parameter.CustomAttributes.All(x => x.AttributeType.FullName != "SiliconStudio.Core.Threading.PooledAttribute"))
                                    continue;

                                // Parameter must be a delegate
                                if (parameter.ParameterType.Resolve().BaseType.FullName != typeof(MulticastDelegate).FullName)
                                    continue;

                                // Find the instruction that pushes the parameter on the stack
                                // Non-trivial control flow is not supported
                                var pushParameterInstruction = WalkStack(instruction, calledMethod.Parameters.Count - parameterIndex);

                                // Try to replace delegate and closure allocations
                                if (pushParameterInstruction?.OpCode == OpCodes.Newobj)
                                    changed |= ProcessDelegateAllocation(context, method, pushParameterInstruction);
                            }
                        }
                    }

                    //// Don't process delegate allocations in factory methods
                    //if (factoryMethods.Contains(method))
                    //    continue;

                    //var isPooled = delegateInstanceType2.Resolve().CustomAttributes.Any(x => x.AttributeType.FullName == "SiliconStudio.Core.Threading.PooledAttribute");

                    //if (!isPooled)
                    //    return false;
                }
            }

            return changed;
        }

        private bool ProcessDelegateAllocation(AssemblyProcessorContext context, MethodDefinition method, Instruction delegateAllocationInstruction)
        {
            // The instruction must be a delegate allocation
            // If not, this might be a static delegate, or some unsupported construct
            if (delegateAllocationInstruction.OpCode != OpCodes.Newobj)
                return false;

            var delegateInstanceConstructor = (MethodReference)delegateAllocationInstruction.Operand;
            var delegateInstanceType = delegateInstanceConstructor.DeclaringType;
            var delegateGenericType = delegateInstanceType.Resolve();
            
            // The previous instruction pushes the delegate method onto the stack
            var functionPointerInstruction = delegateAllocationInstruction.Previous;
            if (functionPointerInstruction.OpCode != OpCodes.Ldftn)
                return false;

            var delegateMethod = (MethodReference)functionPointerInstruction.Operand;

            // The previous instruction pushes the target onto the stack
            // If it's the this-parameter, we can create an instance field, and reuse the same delegate
            var loadClosureInstruction = functionPointerInstruction.Previous;
            if (loadClosureInstruction.OpCode == OpCodes.Ldarg_0 && !method.IsStatic)
            {
                // TODO: Handle generic methods/delegates
                // TODO: Handle multiple constructors propertly
                var constructor = method.DeclaringType.Methods.FirstOrDefault(x => x.Name == ".ctor" && !x.HasParameters);
                var retInstruction3 = constructor?.Body.Instructions.FirstOrDefault(x => x.OpCode == OpCodes.Ret);
                if (retInstruction3 == null)
                    return false;

                // Create an instance field for the shared delegate
                var sharedDelegateField = new FieldDefinition($"<delegate>{delegateMethod.Name}", FieldAttributes.Private, delegateInstanceType);
                method.DeclaringType.Fields.Add(sharedDelegateField);

                // Create and store the delegate in constructor
                var ilProcessor5 = constructor.Body.GetILProcessor();
                ilProcessor5.InsertBefore(retInstruction3, ilProcessor5.Create(OpCodes.Ldarg_0));
                ilProcessor5.InsertBefore(retInstruction3, ilProcessor5.Create(OpCodes.Ldarg_0));
                ilProcessor5.InsertBefore(retInstruction3, ilProcessor5.Create(OpCodes.Ldftn, delegateMethod));
                ilProcessor5.InsertBefore(retInstruction3, ilProcessor5.Create(OpCodes.Newobj, delegateInstanceConstructor));
                ilProcessor5.InsertBefore(retInstruction3, ilProcessor5.Create(OpCodes.Stfld, sharedDelegateField));

                // Load from field instead of allocating
                var ilProcessor4 = method.Body.GetILProcessor();
                ilProcessor4.Remove(functionPointerInstruction);
                ilProcessor4.Replace(delegateAllocationInstruction, ilProcessor4.Create(OpCodes.Ldfld, sharedDelegateField));

                return true;
            }

            // If the target is a compiler generated closure, we only handle local variable load instructions
            int variableIndex;
            OpCode storeOpCode;
            if (!TryGetStoreOpcode(loadClosureInstruction, out storeOpCode, out variableIndex))
                return false;

            // Find the instruction that stores the closure variable
            var storeClosureInstruction = loadClosureInstruction.Previous;
            VariableReference closureVarible = null;

            while (storeClosureInstruction != null)
            {
                closureVarible = storeClosureInstruction.Operand as VariableReference;
                if (storeClosureInstruction.OpCode == storeOpCode && (closureVarible == null || variableIndex == closureVarible.Index))
                    break;

                storeClosureInstruction = storeClosureInstruction.Previous;
            }
            if (storeClosureInstruction == null)
                return false;

            var closureInstanceType = method.Body.Variables[variableIndex].VariableType;
            var closureType = closureInstanceType.Resolve();
            var genericParameters = closureType.GenericParameters.Cast<TypeReference>().ToArray();

            // Patch closure
            ClosureInfo closure;
            if (!closures.TryGetValue(closureType, out closure))
            {
                // Create method initializing new pool items
                var closureTypeConstructor = closureType.Methods.FirstOrDefault(x => x.Name == ".ctor");
                var closureGenericType = closureType.MakeGenericType(genericParameters);
                var factoryMethod = new MethodDefinition("<Factory>", MethodAttributes.HideBySig | MethodAttributes.Private | MethodAttributes.Static, closureGenericType);
                closureType.Methods.Add(factoryMethod);
                factoryMethods.Add(factoryMethod);

                factoryMethod.Body.Variables.Add(new VariableDefinition(closureGenericType));
                var factoryMethodProcessor = factoryMethod.Body.GetILProcessor();
                // Create and store closure
                factoryMethodProcessor.Emit(OpCodes.Newobj, closureTypeConstructor.MakeGeneric(genericParameters));
                factoryMethodProcessor.Emit(OpCodes.Stloc_0);
                //// Return closure
                factoryMethodProcessor.Emit(OpCodes.Ldloc_0);
                factoryMethodProcessor.Emit(OpCodes.Ret);

                // Create pool field
                var poolField = new FieldDefinition("<pool>", FieldAttributes.Public | FieldAttributes.Static, context.Assembly.MainModule.ImportReference(poolType).MakeGenericType(closureGenericType));
                closureType.Fields.Add(poolField);
                var localFieldReference = poolField.MakeGeneric(genericParameters);

                // Initialize pool
                var cctor = GetOrCreateClassConstructor(closureType);
                var ilProcessor2 = cctor.Body.GetILProcessor();
                var retInstruction = cctor.Body.Instructions.FirstOrDefault(x => x.OpCode == OpCodes.Ret);
                ilProcessor2.InsertBefore(retInstruction, ilProcessor2.Create(OpCodes.Ldnull));
                ilProcessor2.InsertBefore(retInstruction, ilProcessor2.Create(OpCodes.Ldftn, factoryMethod.MakeGeneric(genericParameters)));
                ilProcessor2.InsertBefore(retInstruction, ilProcessor2.Create(OpCodes.Newobj, funcConstructor.MakeGeneric(closureGenericType)));
                ilProcessor2.InsertBefore(retInstruction, ilProcessor2.Create(OpCodes.Newobj, poolConstructor.MakeGeneric(closureGenericType)));
                ilProcessor2.InsertBefore(retInstruction, ilProcessor2.Create(OpCodes.Stsfld, localFieldReference));

                // Implement IPooledClosure
                closureType.Interfaces.Add(pooledClosureType);

                var referenceCountField = new FieldDefinition("<referenceCount>", FieldAttributes.Public, context.Assembly.MainModule.TypeSystem.Int32);
                closureType.Fields.Add(referenceCountField);
                var addReferenceMethod = new MethodDefinition("AddReference", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.NewSlot, context.Assembly.MainModule.TypeSystem.Void);
                var ilProcessor4 = addReferenceMethod.Body.GetILProcessor();
                ilProcessor4.Emit(OpCodes.Ldarg_0);
                ilProcessor4.Emit(OpCodes.Ldflda, referenceCountField);
                ilProcessor4.Emit(OpCodes.Call, interlockedIncrementMethod);
                ilProcessor4.Emit(OpCodes.Pop);
                ilProcessor4.Emit(OpCodes.Ret);
                closureType.Methods.Add(addReferenceMethod);

                var releaseMethod = new MethodDefinition("Release", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.NewSlot, context.Assembly.MainModule.TypeSystem.Void);
                ilProcessor4 = releaseMethod.Body.GetILProcessor();
                retInstruction = ilProcessor4.Create(OpCodes.Ret);
                // Check decremented reference count
                ilProcessor4.Emit(OpCodes.Ldarg_0);
                ilProcessor4.Emit(OpCodes.Ldflda, referenceCountField);
                ilProcessor4.Emit(OpCodes.Call, interlockedDecrementMethod);
                ilProcessor4.Emit(OpCodes.Ldc_I4_0);
                ilProcessor4.Emit(OpCodes.Ceq);
                ilProcessor4.Emit(OpCodes.Brfalse_S, retInstruction);
                // Release this to pool
                ilProcessor4.Emit(OpCodes.Ldsfld, localFieldReference);
                ilProcessor4.Emit(OpCodes.Ldarg_0);
                ilProcessor4.Emit(OpCodes.Callvirt, context.Assembly.MainModule.ImportReference(poolType.Methods.FirstOrDefault(x => x.Name == "Release")).MakeGeneric(closureGenericType));
                ilProcessor4.Append(retInstruction);
                closureType.Methods.Add(releaseMethod);

                closures.Add(closureType, closure = new ClosureInfo
                {
                    FactoryMethod = factoryMethod,
                    AddReferenceMethod = addReferenceMethod,
                    ReleaseMethod = releaseMethod,
                    PoolField = poolField
                });
            }

            // Create delegate field
            var delegateFieldType = ChangeGenericArguments(context, delegateInstanceType, closureInstanceType);
            var delegateField = new FieldDefinition($"<delegate>{delegateMethod.Name}", FieldAttributes.Public, delegateFieldType);
            closureType.Fields.Add(delegateField);
            var localDelegateFieldInstance = delegateField.MakeGeneric(genericParameters);

            // Initialize delegate field (the closure instance (local 0) is already on the stack)
            var delegateConstructorInstance = (MethodReference)delegateAllocationInstruction.Operand;
            var delegateGenericArguments = (delegateFieldType as GenericInstanceType)?.GenericArguments.ToArray() ?? new TypeReference[0];
            var genericDelegateConstructor = context.Assembly.MainModule.ImportReference(delegateConstructorInstance.Resolve()).MakeGeneric(delegateGenericArguments);

            var methodInstance = (MethodReference)functionPointerInstruction.Operand;
            var genericMethod = methodInstance.Resolve().MakeGeneric(closureType.GenericParameters.ToArray());

            if (methodInstance is GenericInstanceMethod)
                throw new NotImplementedException();

            var ilProcessor3 = closure.FactoryMethod.Body.GetILProcessor();
            var returnInstruction = ilProcessor3.Body.Instructions.FirstOrDefault(x => x.OpCode == OpCodes.Ret);
            ilProcessor3.InsertBefore(returnInstruction, ilProcessor3.Create(OpCodes.Ldloc_0));
            ilProcessor3.InsertBefore(returnInstruction, ilProcessor3.Create(OpCodes.Ldftn, genericMethod));
            ilProcessor3.InsertBefore(returnInstruction, ilProcessor3.Create(OpCodes.Newobj, genericDelegateConstructor));
            ilProcessor3.InsertBefore(returnInstruction, ilProcessor3.Create(OpCodes.Stfld, localDelegateFieldInstance));
            ilProcessor3.InsertBefore(returnInstruction, ilProcessor3.Create(OpCodes.Ldloc_0));

            var ilProcessor = method.Body.GetILProcessor();

            // Retrieve from pool
            var closureGenericArguments = (closureInstanceType as GenericInstanceType)?.GenericArguments.ToArray() ?? new TypeReference[0];
            var closureAllocation = storeClosureInstruction.Previous;
            if (closureAllocation.OpCode == OpCodes.Newobj)
            {
                // Retrieve closure from pool, instead of allocating
                var acquireClosure = ilProcessor.Create(OpCodes.Callvirt, context.Assembly.MainModule.ImportReference(poolType.Methods.FirstOrDefault(x => x.Name == "Acquire")).MakeGeneric(closureInstanceType));
                ilProcessor.InsertAfter(closureAllocation, acquireClosure);
                ilProcessor.InsertAfter(closureAllocation, ilProcessor.Create(OpCodes.Ldsfld, closure.PoolField.MakeGeneric(closureGenericArguments)));
                closureAllocation.OpCode = OpCodes.Nop; // Change to Nop instead of removing it, as this instruction might be reference somewhere?
                closureAllocation.Operand = null;

                // Add a reference
                ilProcessor.InsertAfter(storeClosureInstruction, ilProcessor.Create(OpCodes.Callvirt, closure.AddReferenceMethod.MakeGeneric(closureGenericArguments)));
                ilProcessor.InsertAfter(storeClosureInstruction, closureVarible == null ? ilProcessor.Create(loadClosureInstruction.OpCode) : ilProcessor.Create(loadClosureInstruction.OpCode, closureVarible.Resolve()));

                // TODO: Multiple returns + try/finally
                // Release reference
                var retInstructions = method.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToArray();

                Instruction beforeReturn;
                ilProcessor.Append(beforeReturn = (closureVarible == null ? ilProcessor.Create(loadClosureInstruction.OpCode) : ilProcessor.Create(loadClosureInstruction.OpCode, closureVarible.Resolve())));
                ilProcessor.Append(ilProcessor.Create(OpCodes.Callvirt, closure.ReleaseMethod.MakeGeneric(closureGenericArguments)));
                ilProcessor.Append(ilProcessor.Create(OpCodes.Ret));

                foreach (var retInstruction2 in retInstructions)
                {
                    retInstruction2.OpCode = OpCodes.Br;
                    retInstruction2.Operand = beforeReturn;
                }


            }

            // Get delegate from closure, instead of allocating
            ilProcessor.Remove(functionPointerInstruction);
            ilProcessor.Replace(delegateAllocationInstruction, ilProcessor.Create(OpCodes.Ldfld, delegateField.MakeGeneric(closureGenericArguments))); // Closure object is already on the stack

            return true;
        }

        private Instruction WalkStack(Instruction instruction, int stackOffset)
        {
            // Walk the stack backwards from the given instruction
            while (stackOffset > 0)
            {
                instruction = instruction.Previous;

                // Calculate what the instruction pushes onto the stack
                int delta;
                if (!TryGetStackPushDelta(instruction, out delta))
                    return null;

                // If this the position on the stack, passed as parameter?
                stackOffset -= delta;
                if (stackOffset == 0)
                    break;

                // Calculate, check what it popped from the stack
                if (!TryGetStackPopDelta(instruction, out delta))
                    return null;

                stackOffset += delta;
            }

            return instruction;
        }

        private static bool TryGetStoreOpcode(Instruction loadInstruction, out OpCode storeOpCode, out int variableIndex)
        {
            variableIndex = 0;
            storeOpCode = default(OpCode);

            var loadOpCode = loadInstruction.OpCode;
            if (loadOpCode == OpCodes.Ldloc_0)
            {
                storeOpCode = OpCodes.Stloc_0;
                variableIndex = 0;
            }
            else if (loadOpCode == OpCodes.Ldloc_1)
            {
                storeOpCode = OpCodes.Stloc_1;
                variableIndex = 1;
            }
            else if (loadOpCode == OpCodes.Ldloc_2)
            {
                storeOpCode = OpCodes.Stloc_2;
                variableIndex = 2;
            }
            else if (loadOpCode == OpCodes.Ldloc_3)
            {
                storeOpCode = OpCodes.Stloc_3;
                variableIndex = 3;
            }
            else if (loadOpCode == OpCodes.Ldloc_S)
            {
                storeOpCode = OpCodes.Stloc_S;
                variableIndex = ((VariableReference)loadInstruction.Operand).Index;
            }
            else if (loadOpCode == OpCodes.Ldloc)
            {
                storeOpCode = OpCodes.Stloc;
                variableIndex = ((VariableReference)loadInstruction.Operand).Index;
            }
            else
            {
                return false;
            }

            return true;
        }

        private MethodDefinition GetOrCreateClassConstructor(TypeDefinition type)
        {
            var cctor = type.Methods.FirstOrDefault(x => x.Name == ".cctor");
            if (cctor == null)
            {
                cctor = new MethodDefinition(".cctor", MethodAttributes.Private
                    | MethodAttributes.HideBySig
                    | MethodAttributes.Static
                    | MethodAttributes.Assembly
                    | MethodAttributes.SpecialName
                    | MethodAttributes.RTSpecialName, type.Module.TypeSystem.Void);
                type.Methods.Add(cctor);
            }

            var retInstruction = cctor.Body.Instructions.FirstOrDefault(x => x.OpCode == OpCodes.Ret);
            if (retInstruction == null)
            {
                var ilProcessor = cctor.Body.GetILProcessor();
                ilProcessor.Emit(OpCodes.Ret);
            }

            return cctor;
        }

        private TypeReference ChangeGenericArguments(AssemblyProcessorContext context, TypeReference type, TypeReference relativeType)
        {
            var genericInstance = type as GenericInstanceType;
            if (genericInstance == null)
                return type;

            var genericArguments = new List<TypeReference>();
            foreach (var genericArgument in genericInstance.GenericArguments)
            {
                if (genericArgument.IsGenericParameter)
                {
                    var genericParameter = GetGenericParameterForArgument(relativeType, genericArgument);
                    if (genericParameter != null)
                    {
                        genericArguments.Add(genericParameter);
                    }
                }
                else
                {
                    var newGenericArgument = ChangeGenericArguments(context, genericArgument, relativeType);
                    genericArguments.Add(newGenericArgument);
                }
            }

            if (genericArguments.Count != genericInstance.GenericArguments.Count)
            {
                throw new InvalidOperationException("Could not resolve generic arguments");
            }

            return context.Assembly.MainModule.ImportReference(genericInstance.Resolve()).MakeGenericInstanceType(genericArguments.ToArray());
        }

        private MethodReference ChangeGenericArguments(AssemblyProcessorContext context, MethodReference method, TypeReference relativeType)
        {
            var genericInstance = method as GenericInstanceMethod;
            if (genericInstance == null)
                return method.Resolve().MakeGeneric(relativeType.Resolve().GenericParameters.ToArray());

            Debugger.Launch();

            var genericArguments = new List<TypeReference>();
            foreach (var genericArgument in genericInstance.GenericArguments)
            {
                if (genericArgument.IsGenericParameter)
                {
                    var genericParameter = GetGenericParameterForArgument(relativeType, genericArgument);
                    if (genericParameter != null)
                    {
                        genericArguments.Add(genericParameter);
                    }
                }
                else
                {
                    var newGenericArgument = ChangeGenericArguments(context, genericArgument, relativeType);
                    genericArguments.Add(newGenericArgument);
                }
            }

            if (genericArguments.Count != genericInstance.GenericArguments.Count)
            {
                throw new InvalidOperationException("Could not resolve generic arguments");
            }

            return context.Assembly.MainModule.ImportReference(genericInstance.Resolve()).MakeGeneric(genericArguments.ToArray());
        }

        private GenericParameter GetGenericParameterForArgument(TypeReference type, TypeReference genericArgument)
        {
            var relativeGenericInstance = type as GenericInstanceType;
            if (relativeGenericInstance == null)
                return null;

            for (int index = 0; index < relativeGenericInstance.GenericArguments.Count; index++)
            {
                var relativeGenericArgument = relativeGenericInstance.GenericArguments[index];
                if (relativeGenericArgument == genericArgument)
                {
                    var genericParameter = relativeGenericInstance.Resolve().GenericParameters[index];
                    return genericParameter;
                }
                else
                {
                    var childParameter = GetGenericParameterForArgument(relativeGenericArgument, genericArgument);
                    if (childParameter != null)
                        return childParameter;
                }
            }

            return null;
        }

        private static bool TryGetStackPushDelta(Instruction instruction, out int delta)
        {
            delta = 0;
            OpCode code = instruction.OpCode;

            switch (code.StackBehaviourPush)
            {
                case StackBehaviour.Push0:
                    delta = 0;
                    break;

                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref:
                    delta = 1;
                    break;

                case StackBehaviour.Push1_push1:
                    delta = 2;
                    break;

                case StackBehaviour.Varpush:
                    if (code.FlowControl == FlowControl.Call)
                    {
                        MethodReference method = (MethodReference)instruction.Operand;
                        delta = (method.ReturnType.MetadataType == MetadataType.Void) ? 0 : 1;
                        break;
                    }
                    return false;

                default:
                    return false;
            }

            return true;
        }

        private static bool TryGetStackPopDelta(Instruction instruction, out int delta)
        {
            delta = 0;
            OpCode code = instruction.OpCode;

            switch (code.StackBehaviourPop)
            {
                case StackBehaviour.Pop0:
                    delta = 0;
                    break;

                case StackBehaviour.Popi:
                case StackBehaviour.Popref:
                case StackBehaviour.Pop1:
                    delta = 1;
                    break;

                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi:
                    delta = 2;
                    break;

                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref:
                    delta = 3;
                    break;

                case StackBehaviour.Varpop:
                    if (code.FlowControl == FlowControl.Call)
                    {
                        MethodReference method = (MethodReference)instruction.Operand;
                        delta = method.Parameters.Count;
                        if (method.HasThis && OpCodes.Newobj.Value != code.Value)
                            delta--;

                        break;
                    }
                    return false;

                default:
                    return false;
            }

            return true;
        }
    }
}