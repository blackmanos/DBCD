﻿using DBFileReaderLib.Attributes;
using System;
using System.Reflection;

namespace DBFileReaderLib
{
    class FieldCache<T>
    {
        public readonly FieldInfo Field;
        public readonly bool IsArray = false;
        public readonly bool IsLocalisedString = false;
        public readonly bool IsRelation = false;
        public readonly Action<T, object> Setter;
        public readonly LocaleAttribute LocaleInfo;

        // Type of the variable that is used to store the field
        // Might not match the information retrieved from client
        // metadata i.e. when field is a relation (as those are always uint32) 
        public readonly Type FieldType;
        // Type of the variable as defined in client metadata
        public readonly Type MetaDataFieldType;

        public bool IndexMapField { get; set; } = false;
        public int Cardinality { get; set; } = 1;

        public FieldCache(FieldInfo field)
        {
            Field = field;
            IsArray = field.FieldType.IsArray;
            IsLocalisedString = GetStringInfo(field, out LocaleInfo);
            Setter = field.GetSetter<T>();
            Cardinality = GetCardinality(field);

            IndexAttribute indexAttribute = (IndexAttribute)Attribute.GetCustomAttribute(field, typeof(IndexAttribute));
            IndexMapField = (indexAttribute != null) ? indexAttribute.NonInline : false;

            RelationAttribute relationAttribute = (RelationAttribute)Attribute.GetCustomAttribute(field, typeof(RelationAttribute));
            IsRelation = (relationAttribute != null);
            FieldType = field.FieldType;
            MetaDataFieldType = IsRelation ? relationAttribute.FieldType : FieldType;
        }

        private int GetCardinality(FieldInfo field)
        {
            var cardinality = field.GetAttribute<CardinalityAttribute>()?.Count;
            return cardinality.HasValue && cardinality > 0 ? cardinality.Value : 1;
        }

        private bool GetStringInfo(FieldInfo field, out LocaleAttribute attribute)
        {
            return (attribute = field.GetAttribute<LocaleAttribute>()) != null;
        }
    }
}
