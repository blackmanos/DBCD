using DBDefsLib;
using DBCD.IO;
using DBCD.IO.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Data;

namespace DBCD
{

    public struct DBCDInfo
    {
		public DataTable Data { get; set; }
        public string tableName;
        public string[] availableColumns;
    }

    internal class DBCDBuilder
    {
        private ModuleBuilder moduleBuilder;
        private int locStringSize;
        private readonly Locale locale;

        internal DBCDBuilder(Locale locale = Locale.None)
        {
            var assemblyName = new AssemblyName("DBCDDefinitons");
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name);

            this.moduleBuilder = moduleBuilder;
            this.locStringSize = 1;
            this.locale = locale;
        }

        internal Tuple<Type, DBCDInfo> Build(DBParser dbcReader, Stream dbd, string name, string build)
        {
            var dbdReader = new DBDReader();

            if (name == null)
            {
                name = Guid.NewGuid().ToString();
            }

            var databaseDefinition = dbdReader.Read(dbd);

            Structs.VersionDefinitions? versionDefinition = null;

            if (!string.IsNullOrWhiteSpace(build))
            {
                var dbBuild = new Build(build);
                locStringSize = GetLocStringSize(dbBuild);
                Utils.GetVersionDefinitionByBuild(databaseDefinition, dbBuild, out versionDefinition);
            }

            if (versionDefinition == null && dbcReader.LayoutHash != 0)
            {
                var layoutHash = dbcReader.LayoutHash.ToString("X8");
                Utils.GetVersionDefinitionByLayoutHash(databaseDefinition, layoutHash, out versionDefinition);
            }

            if (versionDefinition == null)
            {
                throw new FileNotFoundException("No definition found for this file.");
            }

            if (locStringSize > 1 && (int)locale >= locStringSize)
            {
                throw new FormatException("Invalid locale for this file.");
            }

            var typeBuilder = moduleBuilder.DefineType(name, TypeAttributes.Public);

            var fields = versionDefinition.Value.definitions;
            var columns = new List<string>(fields.Length);
            bool localiseStrings = locale != Locale.None;

            // Console.WriteLine($"DBCDBuilder.Build name {name} fields {fields.Length}");

            var info = new DBCDInfo();
			info.Data = new DataTable() { TableName = name + "_" + DBCD.localeNames[(uint)locale], CaseSensitive = false};

            foreach (var fieldDefinition in fields)
            {
                var columnDefinition = databaseDefinition.columnDefinitions[fieldDefinition.name];
                bool isLocalisedString = columnDefinition.type == "locstring" && locStringSize > 1;

                Type fieldType;
                if (fieldDefinition.isRelation && fieldDefinition.isNonInline)
                {
                    fieldType = fieldDefinition.arrLength == 0 ? typeof(int) : typeof(int[]);
                }
                else
                {
                    fieldType = FieldDefinitionToType(fieldDefinition, columnDefinition, localiseStrings, true);
                }

                var field = typeBuilder.DefineField(fieldDefinition.name, fieldType, FieldAttributes.Public);

                // Console.WriteLine($"DBCDBuilder.Build field {fieldDefinition.name} arrLength {fieldDefinition.arrLength} isID {fieldDefinition.isID}");

                columns.Add(fieldDefinition.name);
                if (columnDefinition.type == "locstring" || columnDefinition.type == "string")
                {
                    info.Data.Columns.Add(fieldDefinition.name, fieldType);
                    info.Data.Columns[fieldDefinition.name].AllowDBNull = false;
                    info.Data.Columns[fieldDefinition.name].DefaultValue = string.Empty;
                }
                else if (fieldDefinition.arrLength <= 1)
                {
                    info.Data.Columns.Add(fieldDefinition.name, fieldType);
                    info.Data.Columns[fieldDefinition.name].DefaultValue = 0;
                }
                else
                {
                    for (var j = 0; j < fieldDefinition.arrLength; j++)
                    {
                        string fieldName = fieldDefinition.name + j;
                        info.Data.Columns.Add(fieldName, FieldDefinitionToType(fieldDefinition, columnDefinition, localiseStrings, false));
                        info.Data.Columns[fieldName].DefaultValue = 0;
                    }
                }

                if (fieldDefinition.isID)
                {
                    AddAttribute<IndexAttribute>(field, fieldDefinition.isNonInline);
                    //Setup the Primary Key
                    info.Data.Columns[fieldDefinition.name].DefaultValue = null; //Clear default value
                    info.Data.PrimaryKey = new DataColumn[] { info.Data.Columns[fieldDefinition.name] };
                    info.Data.Columns[fieldDefinition.name].AutoIncrement = true;
                    info.Data.Columns[fieldDefinition.name].Unique = true;
                }

                if (fieldDefinition.arrLength > 1)
                {
                    AddAttribute<CardinalityAttribute>(field, fieldDefinition.arrLength);
                }

                if (fieldDefinition.isRelation)
                {
                    var metaDataFieldType = FieldDefinitionToType(fieldDefinition, columnDefinition, localiseStrings, true);
                    AddAttribute<RelationAttribute>(field, metaDataFieldType, fieldDefinition.isNonInline);
                }

                if (isLocalisedString)
                {
                    if (localiseStrings)
                    {
                        AddAttribute<LocaleAttribute>(field, (int)locale, locStringSize);
                    }
                    else
                    {
                        AddAttribute<CardinalityAttribute>(field, locStringSize);
                        // add locstring mask field
                        typeBuilder.DefineField(fieldDefinition.name + "_mask", typeof(uint), FieldAttributes.Public);
                        columns.Add(fieldDefinition.name + "_mask");

                        info.Data.Columns.Add(fieldDefinition.name + "_mask", typeof(uint)); //Last column is a mask
                        info.Data.Columns[fieldDefinition.name + "_mask"].AllowDBNull = false;
                        info.Data.Columns[fieldDefinition.name + "_mask"].DefaultValue = 0;
                    }
                }
            }

            var type = typeBuilder.CreateTypeInfo();

            info.availableColumns = columns.ToArray();
            info.tableName = name;

            return new Tuple<Type, DBCDInfo>(type, info);
        }

        private int GetLocStringSize(Build build)
        {
            // post wotlk
            if (build.expansion >= 4 || build.build > 12340)
                return 1;

            // tbc - wotlk
            if (build.build >= 6692)
                return 16;

            // alpha - vanilla
            return 8;
        }

        private void AddAttribute<T>(FieldBuilder field, params object[] parameters) where T : Attribute
        {
            var constructorParameters = parameters.Select(x => x.GetType()).ToArray();
            var constructorInfo = typeof(T).GetConstructor(constructorParameters);
            var attributeBuilder = new CustomAttributeBuilder(constructorInfo, parameters);
            field.SetCustomAttribute(attributeBuilder);
        }

        private Type FieldDefinitionToType(Structs.Definition field, Structs.ColumnDefinition column, bool localiseStrings, bool useArray)
        {
            var isArray = field.arrLength != 0 && useArray;

            switch (column.type)
            {
                case "int":
                    {
                        Type type = null;
                        var signed = field.isSigned;

                        switch (field.size)
                        {
                            case 8:
                                type = signed ? typeof(sbyte) : typeof(byte);
                                break;
                            case 16:
                                type = signed ? typeof(short) : typeof(ushort);
                                break;
                            case 32:
                                type = signed ? typeof(int) : typeof(uint);
                                break;
                            case 64:
                                type = signed ? typeof(long) : typeof(ulong);
                                break;
                        }

                        return isArray ? type.MakeArrayType() : type;
                    }
                case "string":
                    return isArray ? typeof(string[]) : typeof(string);
                case "locstring":
                    {
                        if (isArray && locStringSize > 1)
                            throw new NotSupportedException("Localised string arrays are not supported");

                        return (!localiseStrings && locStringSize > 1) || isArray ? typeof(string[]) : typeof(string);
                    }
                case "float":
                    return isArray ? typeof(float[]) : typeof(float);
                default:
                    throw new ArgumentException("Unable to construct C# type from " + column.type);
            }
        }
    }
}
