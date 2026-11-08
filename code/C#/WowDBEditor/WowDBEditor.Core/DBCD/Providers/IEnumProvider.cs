using DBDefsLib.Constants;
using DBDefsLib.Structs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DBCD.Providers
{
    public interface IEnumProvider
    {
        /// <summary>
        /// The list of <see cref="MappingDefinition"/> instances, this contains the actual mapping of field -> meta type -> meta value etc.
        /// </summary>
        public List<MappingDefinition> Mappings { get; }

        /// <summary>
        /// Returns true if any enum or flag mapping exists for the given field, regardless of array index.
        /// Use this as a fast pre-check before calling <see cref="GetEnumDefinition"/>.
        /// </summary>
        public bool HasEnumDefinition(string tableName, string columnName) =>
            Mappings.Any(m =>
                m.meta != MetaType.COLOR &&
                m.tableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
                m.columnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Returns the <see cref="EnumDefinition"/> for a table column, or null if none is mapped.
        /// For non-array fields, omit <paramref name="arrayIndex"/> (or pass null).
        /// For array fields, pass the element index. A specific-index mapping takes priority over
        /// an "applies to all elements" mapping (indicated by a null <c>arrIndex</c> in the source data).
        /// </summary>
        public EnumDefinition? GetEnumDefinition(string tableName, string columnName, int? arrayIndex = null,
            string conditionalTable = null, string conditionalColumn = null, string conditionalValue = null);
    }
}
