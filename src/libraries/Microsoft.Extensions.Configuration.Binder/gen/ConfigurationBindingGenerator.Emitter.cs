// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using SourceGenerators;

namespace Microsoft.Extensions.Configuration.Binder.SourceGeneration
{
    public sealed partial class ConfigurationBindingGenerator : IIncrementalGenerator
    {
        private sealed partial class Emitter
        {
            private readonly SourceProductionContext _context;
            private readonly SourceGenerationSpec _sourceGenSpec;

            private bool _emitBlankLineBeforeNextStatement;
            private bool _useFullyQualifiedNames;
            private int _valueSuffixIndex;

            private static readonly Regex s_arrayBracketsRegex = new(Regex.Escape("[]"));

            private readonly SourceWriter _writer = new();

            public Emitter(SourceProductionContext context, SourceGenerationSpec sourceGenSpec)
            {
                _context = context;
                _sourceGenSpec = sourceGenSpec;
            }

            public void Emit()
            {
                if (!ShouldEmitBinders())
                {
                    return;
                }

                _writer.WriteLine("""
                    // <auto-generated/>
                    #nullable enable
                    #pragma warning disable CS0612, CS0618 // Suppress warnings about [Obsolete] member usage in generated code.
                    """);
                _writer.WriteLine();

                _useFullyQualifiedNames = true;
                EmitBinder_Extensions_IConfiguration();
                EmitBinder_Extensions_OptionsBuilder();
                EmitBinder_Extensions_IServiceCollection();

                _useFullyQualifiedNames = false;
                Emit_CoreBindingHelper();

                _context.AddSource($"{Identifier.GeneratedConfigurationBinder}.g.cs", _writer.ToSourceText());
            }

            private void EmitBindCoreCall(
                TypeSpec type,
                string memberAccessExpr,
                string configArgExpr,
                InitializationKind initKind,
                Action<string>? writeOnSuccess = null)
            {
                Debug.Assert(type.CanInitialize);

                if (!type.NeedsMemberBinding)
                {
                    EmitObjectInit(memberAccessExpr, initKind);
                    return;
                }

                string tempIdentifier = GetIncrementalIdentifier(Identifier.temp);
                if (initKind is InitializationKind.AssignmentWithNullCheck)
                {
                    _writer.WriteLine($"{type.MinimalDisplayString} {tempIdentifier} = {memberAccessExpr};");
                    EmitBindCoreCall(tempIdentifier, InitializationKind.AssignmentWithNullCheck);
                }
                else if (initKind is InitializationKind.None && type.IsValueType)
                {
                    EmitBindCoreCall(tempIdentifier, InitializationKind.Declaration);
                    _writer.WriteLine($"{memberAccessExpr} = {tempIdentifier};");
                }
                else
                {
                    EmitBindCoreCall(memberAccessExpr, initKind);
                }

                void EmitBindCoreCall(string objExpression, InitializationKind initKind)
                {
                    string methodDisplayString = GetHelperMethodDisplayString(nameof(MethodsToGen_CoreBindingHelper.BindCore));
                    string bindCoreCall = $@"{methodDisplayString}({configArgExpr}, ref {objExpression}, {Identifier.binderOptions});";

                    EmitObjectInit(objExpression, initKind);
                    _writer.WriteLine(bindCoreCall);
                    writeOnSuccess?.Invoke(objExpression);
                }

                void EmitObjectInit(string objExpression, InitializationKind initKind)
                {
                    if (initKind is not InitializationKind.None)
                    {
                        this.EmitObjectInit(type, objExpression, initKind, configArgExpr);
                    }
                }
            }

            private void EmitBindLogicFromString(
                ParsableFromStringSpec type,
                string sectionValueExpr,
                string sectionPathExpr,
                Action<string>? writeOnSuccess,
                bool checkForNullSectionValue,
                bool useIncrementalStringValueIdentifier)
            {
                StringParsableTypeKind typeKind = type.StringParsableTypeKind;
                Debug.Assert(typeKind is not StringParsableTypeKind.None);

                string nonNull_StringValue_Identifier = useIncrementalStringValueIdentifier ? GetIncrementalIdentifier(Identifier.value) : Identifier.value;
                string stringValueToParse_Expr = checkForNullSectionValue ? nonNull_StringValue_Identifier : sectionValueExpr;

                string parsedValueExpr;
                if (typeKind is StringParsableTypeKind.AssignFromSectionValue)
                {
                    parsedValueExpr = stringValueToParse_Expr;
                }
                else if (typeKind is StringParsableTypeKind.Enum)
                {
                    parsedValueExpr = $"ParseEnum<{type.MinimalDisplayString}>({stringValueToParse_Expr}, () => {sectionPathExpr})";
                }
                else
                {
                    string helperMethodDisplayString = GetHelperMethodDisplayString(type.ParseMethodName);
                    parsedValueExpr = $"{helperMethodDisplayString}({stringValueToParse_Expr}, () => {sectionPathExpr})";
                }

                if (!checkForNullSectionValue)
                {
                    InvokeWriteOnSuccess();
                }
                else
                {
                    EmitStartBlock($"if ({sectionValueExpr} is string {nonNull_StringValue_Identifier})");
                    InvokeWriteOnSuccess();
                    EmitEndBlock();
                }

                void InvokeWriteOnSuccess() => writeOnSuccess?.Invoke(parsedValueExpr);
            }

            private bool EmitObjectInit(TypeSpec type, string memberAccessExpr, InitializationKind initKind, string configArgExpr)
            {
                Debug.Assert(type.CanInitialize && initKind is not InitializationKind.None);

                string initExpr;
                CollectionSpec? collectionType = type as CollectionSpec;

                string effectiveDisplayString = GetTypeDisplayString(type);
                if (collectionType is not null)
                {
                    if (collectionType is EnumerableSpec { InitializationStrategy: InitializationStrategy.Array })
                    {
                        initExpr = $"new {s_arrayBracketsRegex.Replace(effectiveDisplayString, "[0]", 1)}";
                    }
                    else
                    {
                        effectiveDisplayString = GetTypeDisplayString(collectionType.ConcreteType ?? collectionType);
                        initExpr = $"new {effectiveDisplayString}()";
                    }
                }
                else if (type.InitializationStrategy is InitializationStrategy.ParameterlessConstructor)
                {
                    initExpr = $"new {effectiveDisplayString}()";
                }
                else
                {
                    Debug.Assert(type.InitializationStrategy is InitializationStrategy.ParameterizedConstructor);
                    string initMethodIdentifier = GetInitalizeMethodDisplayString(((ObjectSpec)type));
                    initExpr = $"{initMethodIdentifier}({configArgExpr}, {Identifier.binderOptions})";
                }

                if (initKind == InitializationKind.Declaration)
                {
                    Debug.Assert(!memberAccessExpr.Contains("."));
                    _writer.WriteLine($"var {memberAccessExpr} = {initExpr};");
                }
                else if (initKind == InitializationKind.AssignmentWithNullCheck)
                {
                    if (collectionType is CollectionSpec
                        {
                            InitializationStrategy: InitializationStrategy.ParameterizedConstructor or InitializationStrategy.ToEnumerableMethod
                        })
                    {
                        if (collectionType.InitializationStrategy is InitializationStrategy.ParameterizedConstructor)
                        {
                            _writer.WriteLine($"{memberAccessExpr} = {memberAccessExpr} is null ? {initExpr} : new {effectiveDisplayString}({memberAccessExpr});");
                        }
                        else
                        {
                            _writer.WriteLine($"{memberAccessExpr} = {memberAccessExpr} is null ? {initExpr} : {memberAccessExpr}.{collectionType.ToEnumerableMethodCall!};");
                        }
                    }
                    else
                    {
                        _writer.WriteLine($"{memberAccessExpr} ??= {initExpr};");
                    }
                }
                else
                {
                    Debug.Assert(initKind is InitializationKind.SimpleAssignment);
                    _writer.WriteLine($"{memberAccessExpr} = {initExpr};");
                }

                return true;
            }

            private void EmitCastToIConfigurationSection()
            {
                string sectionTypeDisplayString;
                string exceptionTypeDisplayString;
                if (_useFullyQualifiedNames)
                {
                    sectionTypeDisplayString = "global::Microsoft.Extensions.Configuration.IConfigurationSection";
                    exceptionTypeDisplayString = FullyQualifiedDisplayString.InvalidOperationException;
                }
                else
                {
                    sectionTypeDisplayString = Identifier.IConfigurationSection;
                    exceptionTypeDisplayString = nameof(InvalidOperationException);
                }

                _writer.WriteLine($$"""
                    if ({{Identifier.configuration}} is not {{sectionTypeDisplayString}} {{Identifier.section}})
                    {
                        throw new {{exceptionTypeDisplayString}}();
                    }
                    """);
            }

            private void EmitIConfigurationHasValueOrChildrenCheck(bool voidReturn)
            {
                string returnPostfix = voidReturn ? string.Empty : " null";
                string methodDisplayString = GetHelperMethodDisplayString(Identifier.HasValueOrChildren);

                _writer.WriteLine($$"""
                    if (!{{methodDisplayString}}({{Identifier.configuration}}))
                    {
                        return{{returnPostfix}};
                    }
                    """);
                _writer.WriteLine();
            }
        }
    }
}
