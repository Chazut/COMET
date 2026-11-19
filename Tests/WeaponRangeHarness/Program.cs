using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var syntax = CSharpSyntaxTree
    .ParseText(File.ReadAllText(Path.Combine(root, "Client/WeaponRangeAdapter.cs")))
    .GetRoot();
var names = new[] { "ReadRangeGun", "RangeAttachmentType" };
var methods = syntax
    .DescendantNodes()
    .OfType<MethodDeclarationSyntax>()
    .Where(x => names.Contains(x.Identifier.Text));
var fields = syntax
    .DescendantNodes()
    .OfType<FieldDeclarationSyntax>()
    .Where(x =>
        x.Declaration.Variables.Any(v =>
            v.Identifier.Text.StartsWith("Range") && v.Identifier.Text.EndsWith("Types")
        )
    );
string source =
    "using System;using System.Linq;using System.Collections.Generic;using Comet.Core;using EFT.InventoryLogic;public partial class Probe {"
    + string.Join("\n", fields.Select(x => x.ToString()).Concat(methods.Select(x => x.ToString())))
    + "}";
var trees = new[]
{
    CSharpSyntaxTree.ParseText(source),
    CSharpSyntaxTree.ParseText(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Cases.cs"))
    ),
};
var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
    .Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p))
    .ToList();
refs.Add(MetadataReference.CreateFromFile(typeof(Comet.Core.WeaponRangeGun).Assembly.Location));
var build = CSharpCompilation.Create(
    "WeaponRangeAdapterRegression",
    trees,
    refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
);
using var bytes = new MemoryStream();
var result = build.Emit(bytes);
if (!result.Success)
    throw new Exception(string.Join("\n", result.Diagnostics));
Assembly.Load(bytes.ToArray()).GetType("Probe")!.GetMethod("Run")!.Invoke(null, null);
