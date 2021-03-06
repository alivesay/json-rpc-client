// © Alexander Kozlenko. Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;

using Anemonis.JsonRpc.ServiceClient.Resources;

namespace Anemonis.JsonRpc.ServiceClient
{
    /// <summary>Represents a factory of interface-defined JSON-RPC client instances.</summary>
    public static partial class JsonRpcClientFactory
    {
        private static readonly ConcurrentDictionary<Type, Type> s_types = new();
        private static readonly ModuleBuilder s_moduleBuilder = CreateModuleBuilder();

        /// <summary>Creates an instance of an interface-defined JSON-RPC client.</summary>
        /// <typeparam name="T">The type of the interface which defines JSON-RPC methods.</typeparam>
        /// <param name="executor">The <see cref="JsonRpcClient" /> instance, which executes JSON-RPC requests.</param>
        /// <returns>A new instance of JSON-RPC client defined by the specified interface.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="executor" /> is <see langword="null" />.</exception>
        /// <exception cref="InvalidOperationException">An error occurred while building a type for the specified interface.</exception>
        public static T Create<T>(JsonRpcClient executor)
            where T : class
        {
            if (executor is null)
            {
                throw new ArgumentNullException(nameof(executor));
            }
            if (!typeof(T).IsInterface)
            {
                throw new InvalidOperationException(Strings.GetString("factory.type_is_not_interface"));
            }

            return (T)Activator.CreateInstance(s_types.GetOrAdd(typeof(T), CreateType), executor);
        }

        /// <summary>Creates an instance of an interface-defined JSON-RPC client.</summary>
        /// <typeparam name="T">The type of the interface which defines JSON-RPC methods.</typeparam>
        /// <param name="serviceUri">The service URI.</param>
        /// <returns>A new instance of JSON-RPC client defined by the specified interface.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="serviceUri" /> is <see langword="null" />.</exception>
        /// <exception cref="FormatException"><paramref name="serviceUri" /> is relative, or has invalid scheme, or is not correctly formed.</exception>
        /// <exception cref="InvalidOperationException">An error occurred while building a type for the specified interface.</exception>
        public static T Create<T>(string serviceUri)
            where T : class
        {
            return Create<T>(new JsonRpcClient(serviceUri));
        }

        /// <summary>Creates an instance of an interface-defined JSON-RPC client.</summary>
        /// <typeparam name="T">The type of the interface which defines JSON-RPC methods.</typeparam>
        /// <param name="serviceUri">The service URI.</param>
        /// <returns>A new instance of JSON-RPC client defined by the specified interface.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="serviceUri" /> is <see langword="null" />.</exception>
        /// <exception cref="FormatException"><paramref name="serviceUri" /> is relative, or has invalid scheme, or is not correctly formed.</exception>
        /// <exception cref="InvalidOperationException">An error occurred while building a type for the specified interface.</exception>
        public static T Create<T>(Uri serviceUri)
            where T : class
        {
            return Create<T>(new JsonRpcClient(serviceUri));
        }

        private static Type CreateType(Type interfaceType)
        {
            var contracts = new Dictionary<MethodInfoKey, (JsonRpcMethodAttribute, Type, Type[], bool)>();

            GetContracts(interfaceType, contracts);

            var proxyType = typeof(JsonRpcClientProxy);
            var typeName = $"{nameof(JsonRpcClient)}<{interfaceType.FullName}>";
            var typeAttributes = TypeAttributes.Sealed | TypeAttributes.NotPublic;
            var typeBuilder = s_moduleBuilder.DefineType(typeName, typeAttributes, typeof(JsonRpcClientProxy), new[] { interfaceType });
            var methodAttributes = MethodAttributes.Virtual | MethodAttributes.Public;
            var parametersFactoryMethodT1 = proxyType.GetMethod(nameof(JsonRpcClientProxy.CreateParametersT1));
            var parametersFactoryMethodT2 = proxyType.GetMethod(nameof(JsonRpcClientProxy.CreateParametersT2));
            var parametersStorageT2Type = typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(object));
            var parametersStorageT2AddMethod = parametersStorageT2Type.GetMethod(nameof(Dictionary<string, object>.Add));
            var constructorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, new[] { typeof(JsonRpcClient) });
            var constructorEmitter = constructorBuilder.GetILGenerator();
            var constructorBindingFlags = BindingFlags.NonPublic | BindingFlags.Instance;

            constructorEmitter.Emit(OpCodes.Ldarg_0);
            constructorEmitter.Emit(OpCodes.Ldarg_1);
            constructorEmitter.Emit(OpCodes.Call, proxyType.GetConstructors(constructorBindingFlags).FirstOrDefault());
            constructorEmitter.Emit(OpCodes.Ret);

            foreach (var kvp in contracts)
            {
                var contractAttribute = kvp.Value.Item1;
                var contractResultType = kvp.Value.Item2;
                var contractParameterTypes = kvp.Value.Item3;
                var contractHasCancellationToken = kvp.Value.Item4;
                var methodName = kvp.Key.MethodName;
                var methodReturnType = contractResultType is null ? typeof(Task) : typeof(Task<>).MakeGenericType(contractResultType);
                var methodParameterTypes = GetProxyMethodParameterTypes(contractParameterTypes, contractHasCancellationToken);
                var methodBuilder = typeBuilder.DefineMethod(methodName, methodAttributes, methodReturnType, methodParameterTypes);
                var methodEmitter = methodBuilder.GetILGenerator();
                var proxyMethod = GetProxyMethod(contractAttribute, contractResultType, contractHasCancellationToken);

                switch (contractAttribute.ParametersType)
                {
                    case JsonRpcParametersType.None:
                        {
                            methodEmitter.Emit(OpCodes.Ldarg_0);
                            methodEmitter.Emit(OpCodes.Ldstr, contractAttribute.MethodName);

                            if (contractHasCancellationToken)
                            {
                                methodEmitter.Emit(OpCodes.Ldarg, contractParameterTypes.Length + 1);
                            }

                            methodEmitter.Emit(OpCodes.Call, proxyMethod);
                            methodEmitter.Emit(OpCodes.Ret);
                        }
                        break;
                    case JsonRpcParametersType.ByPosition:
                        {
                            methodEmitter.DeclareLocal(typeof(object[]));
                            methodEmitter.Emit(OpCodes.Ldc_I4, contractParameterTypes.Length);
                            methodEmitter.Emit(OpCodes.Call, parametersFactoryMethodT1);
                            methodEmitter.Emit(OpCodes.Stloc_0);

                            for (var i = 0; i < contractParameterTypes.Length; i++)
                            {
                                methodEmitter.Emit(OpCodes.Ldloc_0);
                                methodEmitter.Emit(OpCodes.Ldc_I4, contractAttribute.ParameterPositions[i]);
                                methodEmitter.Emit(OpCodes.Ldarg, i + 1);

                                if (contractParameterTypes[i].IsValueType)
                                {
                                    methodEmitter.Emit(OpCodes.Box, contractParameterTypes[i]);
                                }

                                methodEmitter.Emit(OpCodes.Stelem_Ref);
                            }

                            methodEmitter.Emit(OpCodes.Ldarg_0);
                            methodEmitter.Emit(OpCodes.Ldstr, contractAttribute.MethodName);
                            methodEmitter.Emit(OpCodes.Ldloc_0);

                            if (contractHasCancellationToken)
                            {
                                methodEmitter.Emit(OpCodes.Ldarg, contractParameterTypes.Length + 1);
                            }

                            methodEmitter.Emit(OpCodes.Call, proxyMethod);
                            methodEmitter.Emit(OpCodes.Ret);
                        }
                        break;
                    case JsonRpcParametersType.ByName:
                        {
                            methodEmitter.DeclareLocal(typeof(Dictionary<string, object>));
                            methodEmitter.Emit(OpCodes.Ldc_I4, contractParameterTypes.Length);
                            methodEmitter.Emit(OpCodes.Call, parametersFactoryMethodT2);
                            methodEmitter.Emit(OpCodes.Stloc_0);

                            for (var i = 0; i < contractParameterTypes.Length; i++)
                            {
                                methodEmitter.Emit(OpCodes.Ldloc_0);
                                methodEmitter.Emit(OpCodes.Ldstr, contractAttribute.ParameterNames[i]);
                                methodEmitter.Emit(OpCodes.Ldarg, i + 1);

                                if (contractParameterTypes[i].IsValueType)
                                {
                                    methodEmitter.Emit(OpCodes.Box, contractParameterTypes[i]);
                                }

                                methodEmitter.Emit(OpCodes.Call, parametersStorageT2AddMethod);
                            }

                            methodEmitter.Emit(OpCodes.Ldarg_0);
                            methodEmitter.Emit(OpCodes.Ldstr, contractAttribute.MethodName);
                            methodEmitter.Emit(OpCodes.Ldloc_0);

                            if (contractHasCancellationToken)
                            {
                                methodEmitter.Emit(OpCodes.Ldarg, contractParameterTypes.Length + 1);
                            }

                            methodEmitter.Emit(OpCodes.Call, proxyMethod);
                            methodEmitter.Emit(OpCodes.Ret);
                        }
                        break;
                }
            }

            return typeBuilder.CreateType();
        }

        private static void GetContracts(Type interfaceType, IDictionary<MethodInfoKey, (JsonRpcMethodAttribute, Type, Type[], bool)> contracts)
        {
            if (interfaceType.IsAssignableFrom(typeof(JsonRpcClientProxy)))
            {
                return;
            }

            if (interfaceType.GetProperties().Any() || interfaceType.GetEvents().Any())
            {
                var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.interface_has_unsupported_members"),
                    interfaceType.FullName, interfaceType.Assembly.FullName);

                throw new InvalidOperationException(exceptionMessage);
            }

            foreach (var method in interfaceType.GetMethods())
            {
                if (method.IsStatic)
                {
                    continue;
                }

                var methodParameters = method.GetParameters();
                var methodKey = new MethodInfoKey(method.Name, methodParameters);

                if (contracts.ContainsKey(methodKey))
                {
                    continue;
                }

                var attribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();

                if (attribute is null)
                {
                    var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.method.attribute_not_found"),
                        method.Name, interfaceType.FullName, interfaceType.Assembly.FullName);

                    throw new InvalidOperationException(exceptionMessage);
                }

                var resultType = default(Type);

                if (!method.ReturnType.IsGenericType)
                {
                    if (method.ReturnType != typeof(Task))
                    {
                        var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.method.invalid_return_type"),
                            method.Name, interfaceType.FullName, interfaceType.Assembly.FullName);

                        throw new InvalidOperationException(exceptionMessage);
                    }
                }
                else
                {
                    if (method.ReturnType.GetGenericTypeDefinition() != typeof(Task<>))
                    {
                        var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.method.invalid_return_type"),
                            method.Name, interfaceType.FullName, interfaceType.Assembly.FullName);

                        throw new InvalidOperationException(exceptionMessage);
                    }

                    resultType = method.ReturnType.GenericTypeArguments[0];
                }

                for (var i = 0; i < methodParameters.Length; i++)
                {
                    if (methodParameters[i].ParameterType.IsByRef)
                    {
                        var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.method.invalid_parameter_modifier"),
                            methodParameters[i].Name, method.Name, interfaceType.FullName, interfaceType.Assembly.FullName);

                        throw new InvalidOperationException(exceptionMessage);
                    }
                }

                var hasCancellationToken = false;
                var contractParametersCount = 0;

                if (methodParameters.Length != 0)
                {
                    hasCancellationToken = methodParameters[^1].ParameterType == typeof(CancellationToken);
                    contractParametersCount = hasCancellationToken ? methodParameters.Length - 1 : methodParameters.Length;
                }

                switch (attribute.ParametersType)
                {
                    case JsonRpcParametersType.ByPosition:
                        {
                            var parameterPositions = attribute.ParameterPositions;

                            if (parameterPositions.Length != contractParametersCount)
                            {
                                var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.method.invalid_parameters_count"),
                                    method.Name, interfaceType.FullName, interfaceType.Assembly.FullName);

                                throw new InvalidOperationException(exceptionMessage);
                            }

                            for (var i = 0; i < parameterPositions.Length; i++)
                            {
                                if (!parameterPositions.Contains(i))
                                {
                                    var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.method.invalid_parameter_positions"),
                                        method.Name, interfaceType.FullName, interfaceType.Assembly.FullName);

                                    throw new InvalidOperationException(exceptionMessage);
                                }
                            }
                        }
                        break;
                    case JsonRpcParametersType.ByName:
                        {
                            var parameterNames = attribute.ParameterNames;

                            if (parameterNames.Length != contractParametersCount)
                            {
                                var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.method.invalid_parameters_count"),
                                    method.Name, interfaceType.FullName, interfaceType.Assembly.FullName);

                                throw new InvalidOperationException(exceptionMessage);
                            }
                            if (parameterNames.Length != parameterNames.Distinct(StringComparer.Ordinal).Count())
                            {
                                var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.method.invalid_parameter_names"),
                                    method.Name, interfaceType.FullName, interfaceType.Assembly.FullName);

                                throw new InvalidOperationException(exceptionMessage);
                            }
                        }
                        break;
                    default:
                        {
                            if (contractParametersCount != 0)
                            {
                                var exceptionMessage = string.Format(CultureInfo.CurrentCulture, Strings.GetString("factory.method.invalid_parameters_count"),
                                    method.Name, interfaceType.FullName, interfaceType.Assembly.FullName);

                                throw new InvalidOperationException(exceptionMessage);
                            }
                        }
                        break;
                }

                var contractParameterTypes = new Type[contractParametersCount];

                for (var i = 0; i < contractParameterTypes.Length; i++)
                {
                    contractParameterTypes[i] = methodParameters[i].ParameterType;
                }

                contracts.Add(methodKey, (attribute, resultType, contractParameterTypes, hasCancellationToken));
            }

            foreach (var implementedInterfaceType in interfaceType.GetInterfaces())
            {
                GetContracts(implementedInterfaceType, contracts);
            }
        }

        private static ModuleBuilder CreateModuleBuilder()
        {
            var currentAssemblyName = typeof(JsonRpcClient).Assembly.GetName();
            var dynamicAssemblyName = new AssemblyName(currentAssemblyName.Name + ".Dynamic");

            dynamicAssemblyName.SetPublicKey(currentAssemblyName.GetPublicKey());
            dynamicAssemblyName.SetPublicKeyToken(currentAssemblyName.GetPublicKeyToken());
            dynamicAssemblyName.Version = currentAssemblyName.Version;

            var dynamicAssemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(dynamicAssemblyName, AssemblyBuilderAccess.RunAndCollect);
            var dynamicModuleBuilder = dynamicAssemblyBuilder.DefineDynamicModule(dynamicAssemblyName.Name);

            return dynamicModuleBuilder;
        }

        private static Type[] GetProxyMethodParameterTypes(Type[] contractParameterTypes, bool hasCancellationToken)
        {
            if (!hasCancellationToken)
            {
                return contractParameterTypes;
            }
            else
            {
                var methodParameterTypes = new Type[contractParameterTypes.Length + 1];

                Array.Copy(contractParameterTypes, 0, methodParameterTypes, 0, contractParameterTypes.Length);

                methodParameterTypes[^1] = typeof(CancellationToken);

                return methodParameterTypes;
            }
        }

        private static MethodInfo GetProxyMethod(JsonRpcMethodAttribute contractAttribute, Type resultType, bool hasCancellationToken)
        {
            var proxyType = typeof(JsonRpcClientProxy);
            var proxyMethodName = default(string);

            if (resultType is null)
            {
                switch (contractAttribute.ParametersType)
                {
                    case JsonRpcParametersType.ByPosition:
                        {
                            proxyMethodName = !hasCancellationToken ?
                                nameof(JsonRpcClientProxy.InvokeT010Async) :
                                nameof(JsonRpcClientProxy.InvokeT011Async);
                        }
                        break;
                    case JsonRpcParametersType.ByName:
                        {
                            proxyMethodName = !hasCancellationToken ?
                                nameof(JsonRpcClientProxy.InvokeT020Async) :
                                nameof(JsonRpcClientProxy.InvokeT021Async);
                        }
                        break;
                    default:
                        {
                            proxyMethodName = !hasCancellationToken ?
                                nameof(JsonRpcClientProxy.InvokeT000Async) :
                                nameof(JsonRpcClientProxy.InvokeT001Async);
                        }
                        break;
                }

                return proxyType.GetMethod(proxyMethodName);
            }
            else
            {
                if (contractAttribute.ErrorDataType is null)
                {
                    switch (contractAttribute.ParametersType)
                    {
                        case JsonRpcParametersType.ByPosition:
                            {
                                proxyMethodName = !hasCancellationToken ?
                                    nameof(JsonRpcClientProxy.InvokeT110Async) :
                                    nameof(JsonRpcClientProxy.InvokeT111Async);
                            }
                            break;
                        case JsonRpcParametersType.ByName:
                            {
                                proxyMethodName = !hasCancellationToken ?
                                    nameof(JsonRpcClientProxy.InvokeT120Async) :
                                    nameof(JsonRpcClientProxy.InvokeT121Async);
                            }
                            break;
                        default:
                            {
                                proxyMethodName = !hasCancellationToken ?
                                    nameof(JsonRpcClientProxy.InvokeT100Async) :
                                    nameof(JsonRpcClientProxy.InvokeT101Async);
                            }
                            break;
                    }

                    return proxyType.GetMethod(proxyMethodName).MakeGenericMethod(resultType);
                }
                else
                {
                    switch (contractAttribute.ParametersType)
                    {
                        case JsonRpcParametersType.ByPosition:
                            {
                                proxyMethodName = !hasCancellationToken ?
                                    nameof(JsonRpcClientProxy.InvokeT210Async) :
                                    nameof(JsonRpcClientProxy.InvokeT211Async);
                            }
                            break;
                        case JsonRpcParametersType.ByName:
                            {
                                proxyMethodName = !hasCancellationToken ?
                                    nameof(JsonRpcClientProxy.InvokeT220Async) :
                                    nameof(JsonRpcClientProxy.InvokeT221Async);
                            }
                            break;
                        default:
                            {
                                proxyMethodName = !hasCancellationToken ?
                                    nameof(JsonRpcClientProxy.InvokeT200Async) :
                                    nameof(JsonRpcClientProxy.InvokeT201Async);
                            }
                            break;
                    }

                    return proxyType.GetMethod(proxyMethodName).MakeGenericMethod(resultType, contractAttribute.ErrorDataType);
                }
            }
        }
    }
}
