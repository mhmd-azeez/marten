using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Marten.Codegen;
using Marten.Schema.Hierarchies;
using Marten.Schema.Identity;
using Marten.Util;
using Npgsql;
using NpgsqlTypes;
using Remotion.Linq;

namespace Marten.Schema
{
    public static class DocumentStorageBuilder
    {
        public static IDocumentStorage Build(IDocumentSchema schema, Type documentType)
        {
            return Build(schema, new DocumentMapping(documentType, schema?.StoreOptions ?? new StoreOptions()));
        }

        public static IDocumentStorage Build(IDocumentSchema schema, DocumentMapping mapping)
        {
            return Build(schema, new[] {mapping}).Single();
        }

        public static IEnumerable<IDocumentStorage> Build(IDocumentSchema schema, DocumentMapping[] mappings)
        {
            // Generate the actual source code
            var code = GenerateDocumentStorageCode(mappings);

            var generator = new AssemblyGenerator();

            // Tell the generator which other assemblies that it should be referencing 
            // for the compilation
            generator.ReferenceAssembly(Assembly.GetExecutingAssembly());
            generator.ReferenceAssemblyContainingType<NpgsqlConnection>();
            generator.ReferenceAssemblyContainingType<QueryModel>();
            generator.ReferenceAssemblyContainingType<DbCommand>();
            generator.ReferenceAssemblyContainingType<Component>();
            generator.ReferenceAssemblyContainingType<DbDataReader>();

            mappings.Select(x => x.DocumentType.Assembly).Distinct().Each(assem => generator.ReferenceAssembly(assem));

            // build the new assembly -- this will blow up if there are any
            // compilation errors with the list of errors and the actual code

            var assembly = generator.Generate(code);

            return assembly
                .GetExportedTypes()
                .Where(x => x.IsConcreteTypeOf<IDocumentStorage>())
                .Select(x => BuildStorageObject(schema, mappings, x));
        }

        public static Type DocumentTypeForStorage(this Type storageType)
        {
            return storageType.FindInterfaceThatCloses(typeof(IdAssignment<>)).GetGenericArguments().Single();
        }

        public static IDocumentStorage BuildStorageObject(IDocumentSchema schema, DocumentMapping[] mappings, Type storageType)
        {
            var docType = storageType.DocumentTypeForStorage();

            var mapping = mappings.Single(m => m.DocumentType == docType);

            return BuildStorageObject(schema, storageType, mapping);
        }

        public static IDocumentStorage BuildStorageObject(IDocumentSchema schema, Type storageType, DocumentMapping mapping)
        {
            var arguments = mapping.ToArguments().Select(arg => arg.GetValue(schema)).ToArray();

            var ctor = storageType.GetConstructors().Single();

            return ctor.Invoke(arguments).As<IDocumentStorage>();
        }

        private static readonly Regex _storenameSanitizer = new Regex("<|>", RegexOptions.Compiled);

        public static string GenerateDocumentStorageCode(DocumentMapping[] mappings)
        {
            var writer = new SourceWriter();

            // TODO -- get rid of the magic strings
            var namespaces = new List<string>
            {
                "System",
                "Marten",
                "Marten.Schema",
                "Marten.Schema.Identity",
                "Marten.Services",
                "Marten.Linq",
                "Marten.Util",
                "Npgsql",
                "Remotion.Linq",
                typeof (NpgsqlDbType).Namespace,
                typeof (IEnumerable<>).Namespace,
                typeof(DbDataReader).Namespace,
                typeof(CancellationToken).Namespace,
                typeof(Task).Namespace
            };
            namespaces.AddRange(mappings.Select(x => x.DocumentType.Namespace));

            namespaces.Distinct().OrderBy(x => x).Each(x => writer.WriteLine($"using {x};"));
            writer.BlankLine();

            writer.StartNamespace("Marten.GeneratedCode");

            mappings.Each(x =>
            {
                GenerateDocumentStorage(x, writer);
                writer.BlankLine();
                writer.BlankLine();
            });

            writer.FinishBlock();
            return writer.Code();
        }

        public static void GenerateDocumentStorage(DocumentMapping mapping, SourceWriter writer)
        {
            var upsertFunction = mapping.ToUpsertFunction();

            var typeName = mapping.DocumentType.GetTypeName();
            var storeName = _storenameSanitizer.Replace(mapping.DocumentType.GetPrettyName(), string.Empty);

            var storageArguments = mapping.ToArguments().ToArray();
            var ctorArgs = storageArguments.Select(x => x.ToCtorArgument()).Join(", ");
            var ctorLines = storageArguments.Select(x => x.ToCtorLine()).Join("\n");
            var fields = storageArguments.Select(x => x.ToFieldDeclaration()).Join("\n");

            var baseType = mapping.IsHierarchy() ? "HierarchicalResolver" : "Resolver";

            var callBaseCtor = mapping.IsHierarchy() ? $": base(mapping, {HierarchyArgument.Hierarchy})" : ": base(mapping)";

            writer.Write(
                $@"
BLOCK:public class {storeName}Storage : {baseType}<{typeName}>, IDocumentStorage, IdAssignment<{typeName}>, IResolver<{typeName}>

{fields}

BLOCK:public {storeName}Storage({ctorArgs}) {callBaseCtor}
{ctorLines}
END




BLOCK:public object Assign({typeName} document, out bool assigned)
{mapping.IdStrategy.AssignmentBodyCode(mapping.IdMember)}
return document.{mapping.IdMember.Name};
END

BLOCK:public void Assign({typeName} document, object id)
document.{mapping.IdMember.Name} = ({mapping.IdMember.GetMemberType().FullName})id;
END

BLOCK:public object Retrieve({typeName} document)
return document.{mapping.IdMember.Name};
END


BLOCK:public object Identity(object document)
return (({typeName})document).{mapping.IdMember.Name};
END

{upsertFunction.ToUpdateBatchMethod(typeName)}


END

");
        }

    }
}