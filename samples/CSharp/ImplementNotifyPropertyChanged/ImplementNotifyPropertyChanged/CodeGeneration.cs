﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace ImplementNotifyPropertyChangedCS
{
    internal static class CodeGeneration
    {
        internal static CompilationUnitSyntax ImplementINotifyPropertyChanged(CompilationUnitSyntax root, SemanticModel model, IEnumerable<ExpandablePropertyInfo> properties, Workspace workspace)
        {
            TypeDeclarationSyntax typeDeclaration = properties.First().PropertyDeclaration.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            Dictionary<PropertyDeclarationSyntax, string> backingFieldLookup = Enumerable.ToDictionary(properties, info => info.PropertyDeclaration, info => info.BackingFieldName);

            root = root.ReplaceNodes(properties.Select(p => p.PropertyDeclaration as SyntaxNode).Concat(new[] { typeDeclaration }),
                (original, updated) => original.IsKind(SyntaxKind.PropertyDeclaration)
                    ? ExpandProperty((PropertyDeclarationSyntax)original, backingFieldLookup[(PropertyDeclarationSyntax)original]) as SyntaxNode
                    : ExpandType((TypeDeclarationSyntax)original, (TypeDeclarationSyntax)updated, properties.Where(p => p.NeedsBackingField), model, workspace));

            return root
                .WithUsing("System.Collections.Generic")
                .WithUsing("System.ComponentModel");
        }

        private static CompilationUnitSyntax WithUsing(this CompilationUnitSyntax root, string name)
        {
            if (!root.Usings.Any(u => u.Name.ToString() == name))
            {
                root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(name)).WithAdditionalAnnotations(Formatter.Annotation));
            }

            return root;
        }

        private static TypeDeclarationSyntax ExpandType(TypeDeclarationSyntax original, TypeDeclarationSyntax updated, IEnumerable<ExpandablePropertyInfo> properties, SemanticModel model, Workspace workspace)
        {
            Debug.Assert(original != updated);

            return updated
                .WithBackingFields(properties, workspace)
                .WithBaseType(original, model)
                .WithPropertyChangedEvent(original, model, workspace)
                .WithSetPropertyMethod(original, model, workspace);
        }

        private static TypeDeclarationSyntax WithBackingFields(this TypeDeclarationSyntax node, IEnumerable<ExpandablePropertyInfo> properties, Workspace workspace)
        {
            // generate backing field for auto-props
            foreach (ExpandablePropertyInfo p in properties)
            {
                MemberDeclarationSyntax fieldDecl = GenerateBackingField(p, workspace);

                // put field just before property
                PropertyDeclarationSyntax currentProp = node.DescendantNodes().OfType<PropertyDeclarationSyntax>().First(d => d.Identifier.Text == p.PropertyDeclaration.Identifier.Text);
                node = node.InsertNodesBefore(currentProp, new[] { fieldDecl });
            }

            return node;
        }

        private static MemberDeclarationSyntax GenerateBackingField(ExpandablePropertyInfo property, Workspace workspace)
        {
            SyntaxGenerator g = SyntaxGenerator.GetGenerator(workspace, LanguageNames.CSharp);
            SyntaxNode type = g.TypeExpression(property.Type);

            FieldDeclarationSyntax fieldDecl = (FieldDeclarationSyntax)ParseMember(string.Format("private _fieldType_ {0};", property.BackingFieldName));
            return fieldDecl.ReplaceNode(fieldDecl.Declaration.Type, type).WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static MemberDeclarationSyntax ParseMember(string member)
        {
            MemberDeclarationSyntax decl = ((ClassDeclarationSyntax)SyntaxFactory.ParseCompilationUnit("class x {\r\n" + member + "\r\n}").Members[0]).Members[0];
            return decl.WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static TypeDeclarationSyntax AddMembers(this TypeDeclarationSyntax node, params MemberDeclarationSyntax[] members)
        {
            return AddMembers(node, (IEnumerable<MemberDeclarationSyntax>)members);
        }

        private static TypeDeclarationSyntax AddMembers(this TypeDeclarationSyntax node, IEnumerable<MemberDeclarationSyntax> members)
        {
            if (node is ClassDeclarationSyntax classDecl)
            {
                return classDecl.WithMembers(classDecl.Members.AddRange(members));
            }

            if (node is StructDeclarationSyntax structDecl)
            {
                return structDecl.WithMembers(structDecl.Members.AddRange(members));
            }

            throw new InvalidOperationException();
        }

        private static PropertyDeclarationSyntax ExpandProperty(PropertyDeclarationSyntax property, string backingFieldName)
        {
            if (!ExpansionChecker.TryGetAccessors(property, out AccessorDeclarationSyntax getter, out AccessorDeclarationSyntax setter))
            {
                throw new ArgumentException();
            }

            if (getter.Body == null)
            {
                StatementSyntax returnFieldStatement = SyntaxFactory.ParseStatement(string.Format("return {0};", backingFieldName));
                getter = getter
                    .WithBody(SyntaxFactory.Block(SyntaxFactory.SingletonList(returnFieldStatement)));
            }

            getter = getter
                .WithSemicolonToken(default(SyntaxToken));

            StatementSyntax setPropertyStatement = SyntaxFactory.ParseStatement(string.Format("SetProperty(ref {0}, value, \"{1}\");", backingFieldName, property.Identifier.ValueText));
            setter = setter
                .WithBody(SyntaxFactory.Block(SyntaxFactory.SingletonList(setPropertyStatement)))
                .WithSemicolonToken(default(SyntaxToken));

            PropertyDeclarationSyntax newProperty = property
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { getter, setter })))
                .WithAdditionalAnnotations(Formatter.Annotation);

            return newProperty;
        }

        private const string InterfaceName = "System.ComponentModel.INotifyPropertyChanged";

        private static TypeDeclarationSyntax WithBaseType(this TypeDeclarationSyntax node, TypeDeclarationSyntax original, SemanticModel semanticModel)
        {
            INamedTypeSymbol classSymbol = semanticModel.GetDeclaredSymbol(original);
            INamedTypeSymbol interfaceSymbol = semanticModel.Compilation.GetTypeByMetadataName(InterfaceName);

            // Does this class already implement INotifyPropertyChanged? If not, add it to the base list.
            if (!classSymbol.AllInterfaces.Contains(interfaceSymbol))
            {
                TypeSyntax baseTypeName = SyntaxFactory.ParseTypeName(InterfaceName)
                    .WithAdditionalAnnotations(Simplifier.Annotation);

                node = node.IsKind(SyntaxKind.ClassDeclaration)
                   ? ((ClassDeclarationSyntax)node).AddBaseListTypes(SyntaxFactory.SimpleBaseType(baseTypeName)) as TypeDeclarationSyntax
                   : ((StructDeclarationSyntax)node).AddBaseListTypes(SyntaxFactory.SimpleBaseType(baseTypeName));

                // Add a formatting annotation to the base list to ensure that it gets formatted properly.
                node = node.ReplaceNode(
                    node.BaseList,
                    node.BaseList.WithAdditionalAnnotations(Formatter.Annotation));
            }

            return node;
        }

        private static TypeDeclarationSyntax WithPropertyChangedEvent(this TypeDeclarationSyntax node, TypeDeclarationSyntax original, SemanticModel semanticModel, Workspace workspace)
        {
            INamedTypeSymbol classSymbol = semanticModel.GetDeclaredSymbol(original);
            INamedTypeSymbol interfaceSymbol = semanticModel.Compilation.GetTypeByMetadataName(InterfaceName);
            IEventSymbol propertyChangedEventSymbol = (IEventSymbol)interfaceSymbol.GetMembers("PropertyChanged").Single();
            ISymbol propertyChangedEvent = classSymbol.FindImplementationForInterfaceMember(propertyChangedEventSymbol);

            // Does this class contain an implementation for the PropertyChanged event? If not, add it.
            if (propertyChangedEvent == null)
            {
                MemberDeclarationSyntax propertyChangedEventDecl = GeneratePropertyChangedEvent();
                node = node.AddMembers(propertyChangedEventDecl);
            }

            return node;
        }

        internal static MemberDeclarationSyntax GeneratePropertyChangedEvent()
        {
            EventFieldDeclarationSyntax decl = (EventFieldDeclarationSyntax)ParseMember("public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;");
            return decl.ReplaceNode(decl.Declaration.Type, decl.Declaration.Type.WithAdditionalAnnotations(Simplifier.Annotation));
        }

        private static IMethodSymbol FindSetPropertyMethod(this INamedTypeSymbol classSymbol, Compilation compilation)
        {
            // Find SetProperty<T>(ref T, T, string) method.
            IMethodSymbol setPropertyMethod = classSymbol
                .GetMembers("SetProperty")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Parameters.Length == 3 && m.TypeParameters.Length == 1);

            if (setPropertyMethod != null)
            {
                System.Collections.Immutable.ImmutableArray<IParameterSymbol> parameters = setPropertyMethod.Parameters;
                ITypeParameterSymbol typeParameter = setPropertyMethod.TypeParameters[0];
                INamedTypeSymbol stringType = compilation.GetSpecialType(SpecialType.System_String);

                if (setPropertyMethod.ReturnsVoid &&
                    parameters[0].RefKind == RefKind.Ref &&
                    parameters[0].Type.Equals(typeParameter) &&
                    parameters[1].Type.Equals(typeParameter) &&
                    parameters[2].Type.Equals(stringType))
                {
                    return setPropertyMethod;
                }
            }

            return null;
        }

        private static TypeDeclarationSyntax WithSetPropertyMethod(this TypeDeclarationSyntax node, TypeDeclarationSyntax original, SemanticModel semanticModel, Workspace workspace)
        {
            INamedTypeSymbol classSymbol = semanticModel.GetDeclaredSymbol(original);
            INamedTypeSymbol interfaceSymbol = semanticModel.Compilation.GetTypeByMetadataName(InterfaceName);
            IEventSymbol propertyChangedEventSymbol = (IEventSymbol)interfaceSymbol.GetMembers("PropertyChanged").Single();
            ISymbol propertyChangedEvent = classSymbol.FindImplementationForInterfaceMember(propertyChangedEventSymbol);

            IMethodSymbol setPropertyMethod = classSymbol.FindSetPropertyMethod(semanticModel.Compilation);
            if (setPropertyMethod == null)
            {
                MethodDeclarationSyntax setPropertyDecl = GenerateSetPropertyMethod();
                node = AddMembers(node, setPropertyDecl);
            }

            return node;
        }

        internal static MethodDeclarationSyntax GenerateSetPropertyMethod()
        {
            return (MethodDeclarationSyntax)ParseMember(@"
private void SetProperty<T>(ref T field, T value, string name)
{
    if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
    {
        field = value;

        var handler = PropertyChanged;
        if (handler != null)
        {
            handler(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }
}").WithAdditionalAnnotations(Simplifier.Annotation);
        }
    }
}
