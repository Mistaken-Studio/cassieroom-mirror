// -----------------------------------------------------------------------
// <copyright file="Config.cs" company="Mistaken">
// Copyright (c) Mistaken. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.ComponentModel;
using Mistaken.Updater.Config;

namespace Mistaken.CassieRoom
{
    /// <inheritdoc/>
    public class Config : IAutoUpdatableConfig
    {
        /// <inheritdoc/>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether debug should be displayed.
        /// </summary>
        [Description("If true then debug will be displayed")]
        public bool VerbouseOutput { get; set; }

        /// <inheritdoc/>
        [Description("Auto Update Settings")]
        public System.Collections.Generic.Dictionary<string, string> AutoUpdateConfig { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether SCP1499 chamber should spawn.
        /// </summary>
        [Description("If true then SCP1499 chamber will spawn inside tower")]
        public bool SpawnSCP1499Chamber { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether CassieRoom should spawn.
        /// </summary>
        [Description("If true then Cassie Room will spawn")]
        public bool SpawnCassieRoom { get; set; } = true;
    }
}
