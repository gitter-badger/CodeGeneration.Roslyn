﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.

namespace CodeGeneration.Roslyn
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Validation;

    /// <summary>
    /// The class responsible for generating compilation units to add to the project being built.
    /// </summary>
    public static class DocumentTransform
    {
        private static readonly string GeneratedByAToolPreamble = @"// ------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
// ------------------------------------------------------------------------------
".Replace("\r\n", "\n").Replace("\n", Environment.NewLine); // normalize regardless of git checkout policy

        /// <summary>
        /// Produces a new document in response to any code generation attributes found in the specified document.
        /// </summary>
        /// <param name="compilation">The compilation to which the document belongs.</param>
        /// <param name="inputDocument">The document to scan for generator attributes.</param>
        /// <param name="assemblyLoader">A function that can load an assembly with the given name.</param>
        /// <param name="progress">Reports warnings and errors in code generation.</param>
        /// <returns>A task whose result is the generated document.</returns>
        public static async Task<SyntaxTree> TransformAsync(CSharpCompilation compilation, SyntaxTree inputDocument, string projectDirectory, Func<AssemblyName, Assembly> assemblyLoader, IProgress<Diagnostic> progress)
        {
            Requires.NotNull(compilation, nameof(compilation));
            Requires.NotNull(inputDocument, nameof(inputDocument));
            Requires.NotNull(assemblyLoader, nameof(assemblyLoader));

            var inputSemanticModel = compilation.GetSemanticModel(inputDocument);
            var inputSyntaxTree = inputSemanticModel.SyntaxTree;

            var inputFileLevelUsingDirectives = inputSyntaxTree.GetRoot().ChildNodes().OfType<UsingDirectiveSyntax>();

            var memberNodes = inputSyntaxTree.GetRoot().DescendantNodesAndSelf().OfType<CSharpSyntaxNode>();

            var emittedMembers = SyntaxFactory.List<MemberDeclarationSyntax>();
            foreach (var memberNode in memberNodes)
            {
                var attributeData = GetAttributeData(compilation, inputSemanticModel, memberNode);
                if (attributeData == null)
                    continue;

                var generators = FindCodeGenerators(attributeData.Value, assemblyLoader);
                foreach (var generator in generators)
                {
                    var context = new TransformationContext(memberNode, inputSemanticModel, compilation, projectDirectory);
                    var generatedTypes = await generator.GenerateAsync(context, progress, CancellationToken.None);

                    // Figure out ancestry for the generated type, including nesting types and namespaces.
                    foreach (var ancestor in memberNode.Ancestors())
                    {
                        var ancestorNamespace = ancestor as NamespaceDeclarationSyntax;
                        var nestingClass = ancestor as ClassDeclarationSyntax;
                        var nestingStruct = ancestor as StructDeclarationSyntax;
                        if (ancestorNamespace != null)
                        {
                            generatedTypes = SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                                ancestorNamespace
                                    .WithMembers(generatedTypes)
                                    .WithLeadingTrivia(SyntaxFactory.TriviaList())
                                    .WithTrailingTrivia(SyntaxFactory.TriviaList()));
                        }
                        else if (nestingClass != null)
                        {
                            generatedTypes = SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                                nestingClass
                                    .WithMembers(generatedTypes)
                                    .WithLeadingTrivia(SyntaxFactory.TriviaList())
                                    .WithTrailingTrivia(SyntaxFactory.TriviaList()));
                        }
                        else if (nestingStruct != null)
                        {
                            generatedTypes = SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                                nestingStruct
                                    .WithMembers(generatedTypes)
                                    .WithLeadingTrivia(SyntaxFactory.TriviaList())
                                    .WithTrailingTrivia(SyntaxFactory.TriviaList()));
                        }
                    }

                    emittedMembers = emittedMembers.AddRange(generatedTypes);
                }
            }

            // By default, retain all the using directives that came from the input file.
            var resultFileLevelUsingDirectives = SyntaxFactory.List(inputFileLevelUsingDirectives);

            var compilationUnit = SyntaxFactory.CompilationUnit()
                .WithUsings(resultFileLevelUsingDirectives)
                .WithMembers(emittedMembers)
                .WithLeadingTrivia(SyntaxFactory.Comment(GeneratedByAToolPreamble))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                .NormalizeWhitespace();

            return compilationUnit.SyntaxTree;
        }

        private static ImmutableArray<AttributeData>? GetAttributeData(CSharpCompilation compilation, SemanticModel document, CSharpSyntaxNode memberNode)
        {
            switch (memberNode)
            {
                case CompilationUnitSyntax syntax:
                    var validAttributesName = syntax.AttributeLists
                                                    .SelectMany(x => x.Attributes)
                                                    .Select(x => (x.Name as IdentifierNameSyntax)?.Identifier.ValueText)
                                                    .Where(x => x != null)
                                                    .Select(x => x.EndsWith("Attribute") ? x : x + "Attribute")
                                                    .ToImmutableHashSet();
                    return compilation.Assembly.GetAttributes().Where(x => validAttributesName.Contains(x.AttributeClass.MetadataName)).ToImmutableArray();
                default:
                    return document.GetDeclaredSymbol(memberNode)?.GetAttributes();
            }
        }

        private static IEnumerable<ICodeGenerator> FindCodeGenerators(ImmutableArray<AttributeData> nodeAttributes, Func<AssemblyName, Assembly> assemblyLoader)
        {
            foreach (var attributeData in nodeAttributes)
            {
                Type generatorType = GetCodeGeneratorTypeForAttribute(attributeData.AttributeClass, assemblyLoader);
                if (generatorType != null)
                {
                    ICodeGenerator generator = (ICodeGenerator)Activator.CreateInstance(generatorType, attributeData);
                    yield return generator;
                }
            }
        }

        private static Type GetCodeGeneratorTypeForAttribute(INamedTypeSymbol attributeType, Func<AssemblyName, Assembly> assemblyLoader)
        {
            Requires.NotNull(assemblyLoader, nameof(assemblyLoader));

            if (attributeType != null)
            {
                foreach (var generatorCandidateAttribute in attributeType.GetAttributes())
                {
                    if (generatorCandidateAttribute.AttributeClass.Name == typeof(CodeGenerationAttributeAttribute).Name)
                    {
                        string assemblyName = null;
                        string fullTypeName = null;
                        TypedConstant firstArg = generatorCandidateAttribute.ConstructorArguments.Single();
                        if (firstArg.Value is string typeName)
                        {
                            // This string is the full name of the type, which MAY be assembly-qualified.
                            int commaIndex = typeName.IndexOf(',');
                            bool isAssemblyQualified = commaIndex >= 0;
                            if (isAssemblyQualified)
                            {
                                fullTypeName = typeName.Substring(0, commaIndex);
                                assemblyName = typeName.Substring(commaIndex + 1).Trim();
                            }
                            else
                            {
                                fullTypeName = typeName;
                                assemblyName = generatorCandidateAttribute.AttributeClass.ContainingAssembly.Name;
                            }
                        }
                        else if (firstArg.Value is INamedTypeSymbol typeOfValue)
                        {
                            // This was a typeof(T) expression
                            fullTypeName = GetFullTypeName(typeOfValue);
                            assemblyName = typeOfValue.ContainingAssembly.Name;
                        }

                        if (assemblyName != null)
                        {
                            var assembly = assemblyLoader(new AssemblyName(assemblyName));
                            if (assembly != null)
                            {
                                return assembly.GetType(fullTypeName);
                            }
                        }

                        Verify.FailOperation("Unable to find code generator: {0} in {1}", fullTypeName, assemblyName);
                    }
                }
            }

            return null;
        }

        private static string GetFullTypeName(INamedTypeSymbol symbol)
        {
            Requires.NotNull(symbol, nameof(symbol));

            var nameBuilder = new StringBuilder();
            ISymbol symbolOrParent = symbol;
            while (symbolOrParent != null && !string.IsNullOrEmpty(symbolOrParent.Name))
            {
                if (nameBuilder.Length > 0)
                {
                    nameBuilder.Insert(0, ".");
                }

                nameBuilder.Insert(0, symbolOrParent.Name);
                symbolOrParent = symbolOrParent.ContainingSymbol;
            }

            return nameBuilder.ToString();
        }
    }
}
