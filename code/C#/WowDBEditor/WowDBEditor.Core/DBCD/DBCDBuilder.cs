using DBDefsLib;
using DBDefsLib.Constants;
using DBDefsLib.Structs;
using DBCD.IO;
using DBCD.IO.Attributes;
using DBCD.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace DBCD
{
    public struct DBCDInfo
    {
        public string tableName;
        public string[] availableColumns;
        public IReadOnlyDictionary<string, Type> enumTypes;
    }

    public class DBCDBuilder
    {
        private readonly ModuleBuilder moduleBuilder;
        private readonly Locale locale;
        private readonly IEnumProvider enumProvider;
        private int locStringSize;

        public DBCDBuilder(Locale locale = Locale.None, IEnumProvider enumProvider = null)
        {
            var assemblyName = new AssemblyName("DBCDDefinitions");
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name);

            this.locStringSize = 1;
            this.locale = locale;
            this.enumProvider = enumProvider;
        }

        public (Type Type, DBCDInfo Info) Build(DBParser dbcReader, DBDefinition databaseDefinition, string name, string build)
        {
            name ??= Guid.NewGuid().ToString();

            VersionDefinitions? versionDefinition = null;
            Build currentBuild = null;

            if (!string.IsNullOrWhiteSpace(build))
            {
                currentBuild = new Build(build);
                locStringSize = GetLocStringSize(currentBuild);
                Utils.GetVersionDefinitionByBuild(databaseDefinition, currentBuild, out versionDefinition);
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
            var localiseStrings = locale != Locale.None;
            var enumTypes = new Dictionary<string, Type>();

            foreach (var fieldDefinition in fields)
            {
                var columnDefinition = databaseDefinition.columnDefinitions[fieldDefinition.name];
                var isLocalisedString = columnDefinition.type == "locstring" && locStringSize > 1;

                Type fieldType;
                if (fieldDefinition is { isRelation: true, isNonInline: true })
                {
                    fieldType = fieldDefinition.arrLength == 0 ? typeof(int) : typeof(int[]);
                }
                else
                {
                    fieldType = FieldDefinitionToType(fieldDefinition, columnDefinition, localiseStrings);
                }

                var field = typeBuilder.DefineField(fieldDefinition.name, fieldType, FieldAttributes.Public);

                columns.Add(fieldDefinition.name);

                if (fieldDefinition.isID)
                {
                    AddAttribute<IndexAttribute>(field, fieldDefinition.isNonInline);
                }

                if (fieldDefinition.arrLength > 1)
                {
                    AddAttribute<CardinalityAttribute>(field, fieldDefinition.arrLength);
                }

                if (fieldDefinition.isRelation)
                {
                    var metaDataFieldType = FieldDefinitionToType(fieldDefinition, columnDefinition, localiseStrings);
                    AddAttribute<RelationAttribute>(field, metaDataFieldType, fieldDefinition.isNonInline);
                }

                if (!string.IsNullOrEmpty(columnDefinition.foreignTable))
                {
                    AddAttribute<ForeignReferenceAttribute>(field, columnDefinition.foreignTable, columnDefinition.foreignColumn);
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
                    }
                }

                // Enum/flags annotation — non-relation fields with a known mapping only
                if (enumProvider != null && !fieldDefinition.isRelation && enumProvider.HasEnumDefinition(name, fieldDefinition.name))
                {
                    if (fieldDefinition.arrLength == 0)
                    {
                        // Non-array: single definition applies to the whole field
                        var enumDef = enumProvider.GetEnumDefinition(name, fieldDefinition.name);
                        if (enumDef.HasValue)
                        {
                            var isFlags = enumDef.Value.metaType == MetaType.FLAGS;
                            var enumTypeName = $"{name}_{fieldDefinition.name}";
                            var enumType = BuildEnumType(enumTypeName, enumDef.Value, currentBuild, fieldType);
                            enumTypes[fieldDefinition.name] = enumType;
                            AddEnumAttribute(field, enumTypeName, isFlags);
                        }
                    }
                    else
                    {
                        // Array: query each index individually; specific-index mappings take priority
                        // over "applies to all" mappings (handled inside GetEnumDefinition).
                        EnumDefinition? attributeHint = null;

                        for (var i = 0; i < fieldDefinition.arrLength; i++)
                        {
                            var enumDef = enumProvider.GetEnumDefinition(name, fieldDefinition.name, i);
                            if (!enumDef.HasValue)
                                continue;

                            var enumTypeName = $"{name}_{fieldDefinition.name}_{i}";
                            var enumType = BuildEnumType(enumTypeName, enumDef.Value, currentBuild, fieldType.GetElementType());
                            enumTypes[$"{fieldDefinition.name}[{i}]"] = enumType;
                            attributeHint ??= enumDef;
                        }

                        // Tag the field with EnumAttribute so consumers know to check EnumTypes
                        if (attributeHint.HasValue)
                        {
                            var hintTypeName = $"{name}_{fieldDefinition.name}";
                            AddEnumAttribute(field, hintTypeName, attributeHint.Value.metaType == MetaType.FLAGS);
                        }
                    }
                }
            }

            var type = typeBuilder.CreateTypeInfo();

            var info = new DBCDInfo
            {
                availableColumns = columns.ToArray(),
                tableName = name,
                enumTypes = enumTypes
            };

            return (type, info);
        }

        /// <summary>
        /// Dynamically generates an enum type from an <see cref="EnumDefinition"/>,
        /// optionally filtered to entries that match <paramref name="currentBuild"/>.
        /// </summary>
        private Type BuildEnumType(string typeName, EnumDefinition enumDef, Build currentBuild, Type underlyingType)
        {
            var enumBuilder = moduleBuilder.DefineEnum(typeName, TypeAttributes.Public, underlyingType);

            var isFlags = enumDef.metaType == MetaType.FLAGS;
            if (isFlags)
            {
                var flagsCtor = typeof(FlagsAttribute).GetConstructor(Type.EmptyTypes);
                enumBuilder.SetCustomAttribute(new CustomAttributeBuilder(flagsCtor, []));
            }

            foreach (var entry in enumDef.entries)
            {
                if (string.IsNullOrEmpty(entry.name))
                    continue;

                if (!EntryMatchesBuild(entry, currentBuild))
                    continue;

                // Bit of a hack to return the correct type, unchecked on signed types to not care about overflows (e.g. 0x80000000 for Map::Flags0 FORCE_CUSTOM_UI_MAP)
                object value = null;

                if (underlyingType == typeof(sbyte))
                    value = unchecked((sbyte)entry.value);
                else if (underlyingType == typeof(byte))
                    value = (byte)entry.value;
                else if (underlyingType == typeof(short))
                    value = unchecked((short)entry.value);
                else if (underlyingType == typeof(ushort))
                    value = (ushort)entry.value;
                else if (underlyingType == typeof(int))
                    value = unchecked((int)entry.value);
                else if (underlyingType == typeof(uint))
                    value = (uint)entry.value;
                else if (underlyingType == typeof(long))
                    value = entry.value;
                else if (underlyingType == typeof(ulong))
                    value = unchecked((ulong)entry.value);
                else
                    throw new Exception("Unsupported underlying type for enum: " + underlyingType.ToString());

                enumBuilder.DefineLiteral(entry.name, value);
            }

            return enumBuilder.CreateTypeInfo();
        }

        /// <summary>
        /// Returns true if the entry applies to the given build, or if the entry has no build
        /// restrictions (applies to all builds), or if no build is specified.
        /// </summary>
        private static bool EntryMatchesBuild(EnumEntry entry, Build currentBuild)
        {
            if (currentBuild == null || (entry.builds.Length == 0 && entry.buildRanges.Length == 0))
                return true;

            if (entry.builds.Any(b => b.Equals(currentBuild)))
                return true;

            return entry.buildRanges.Any(r => r.Contains(currentBuild));
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

        /// <summary>
        /// Applies <see cref="EnumAttribute"/> to a field. Uses a dedicated method because the
        /// generic <see cref="AddAttribute{T}"/> helper derives parameter types via
        /// <c>GetType()</c>, which doesn't work correctly for <c>bool</c> literals.
        /// </summary>
        private static void AddEnumAttribute(FieldBuilder field, string enumName, bool isFlags)
        {
            var ctor = typeof(EnumAttribute).GetConstructor([typeof(string), typeof(bool)]);
            var attributeBuilder = new CustomAttributeBuilder(ctor, [enumName, isFlags]);
            field.SetCustomAttribute(attributeBuilder);
        }

        private Type FieldDefinitionToType(Definition field, ColumnDefinition column, bool localiseStrings)
        {
            var isArray = field.arrLength != 0;

            switch (column.type)
            {
                case "int":
                {
                    var signed = field.isSigned;
                    var type = field.size switch
                    {
                        8  => signed ? typeof(sbyte) : typeof(byte),
                        16 => signed ? typeof(short) : typeof(ushort),
                        32 => signed ? typeof(int) : typeof(uint),
                        64 => signed ? typeof(long) : typeof(ulong),
                        _  => null
                    };
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
