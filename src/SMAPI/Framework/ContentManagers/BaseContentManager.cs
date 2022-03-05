#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Reflection;
using StardewValley;
using xTile;

namespace StardewModdingAPI.Framework.ContentManagers
{
    /// <summary>A content manager which handles reading files from a SMAPI mod folder with support for unpacked files.</summary>
    internal abstract class BaseContentManager : LocalizedContentManager, IContentManager
    {
        /*********
        ** Fields
        *********/
        /// <summary>The central coordinator which manages content managers.</summary>
        protected readonly ContentCoordinator Coordinator;

        /// <summary>The underlying asset cache.</summary>
        protected readonly ContentCache Cache;

        /// <summary>Encapsulates monitoring and logging.</summary>
        protected readonly IMonitor Monitor;

        /// <summary>Whether to enable more aggressive memory optimizations.</summary>
        protected readonly bool AggressiveMemoryOptimizations;

        /// <summary>Whether to automatically try resolving keys to a localized form if available.</summary>
        protected bool TryLocalizeKeys = true;

        /// <summary>Whether the content coordinator has been disposed.</summary>
        private bool IsDisposed;

        /// <summary>A callback to invoke when the content manager is being disposed.</summary>
        private readonly Action<BaseContentManager> OnDisposing;

        /// <summary>A list of disposable assets.</summary>
        private readonly List<WeakReference<IDisposable>> Disposables = new();

        /// <summary>The disposable assets tracked by the base content manager.</summary>
        /// <remarks>This should be kept empty to avoid keeping disposable assets referenced forever, which prevents garbage collection when they're unused. Disposable assets are tracked by <see cref="Disposables"/> instead, which avoids a hard reference.</remarks>
        private readonly List<IDisposable> BaseDisposableReferences;

        /// <summary>A cache of proxy wrappers for the <see cref="ContentManager.Load{T}"/> method.</summary>
        private readonly Dictionary<Type, object> BaseLoadProxyCache = new();

        /// <summary>Whether to check the game folder in the base <see cref="DoesAssetExist{T}(IAssetName)"/> implementation.</summary>
        protected bool CheckGameFolderForAssetExists;


        /*********
        ** Accessors
        *********/
        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public LanguageCode Language => this.GetCurrentLanguage();

        /// <inheritdoc />
        public string FullRootDirectory => Path.Combine(Constants.GamePath, this.RootDirectory);

        /// <inheritdoc />
        public bool IsNamespaced { get; }


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="name">A name for the mod manager. Not guaranteed to be unique.</param>
        /// <param name="serviceProvider">The service provider to use to locate services.</param>
        /// <param name="rootDirectory">The root directory to search for content.</param>
        /// <param name="currentCulture">The current culture for which to localize content.</param>
        /// <param name="coordinator">The central coordinator which manages content managers.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        /// <param name="reflection">Simplifies access to private code.</param>
        /// <param name="onDisposing">A callback to invoke when the content manager is being disposed.</param>
        /// <param name="isNamespaced">Whether this content manager handles managed asset keys (e.g. to load assets from a mod folder).</param>
        /// <param name="aggressiveMemoryOptimizations">Whether to enable more aggressive memory optimizations.</param>
        protected BaseContentManager(string name, IServiceProvider serviceProvider, string rootDirectory, CultureInfo currentCulture, ContentCoordinator coordinator, IMonitor monitor, Reflector reflection, Action<BaseContentManager> onDisposing, bool isNamespaced, bool aggressiveMemoryOptimizations)
            : base(serviceProvider, rootDirectory, currentCulture)
        {
            // init
            this.Name = name;
            this.Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            this.Cache = new ContentCache(this, reflection);
            this.Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            this.OnDisposing = onDisposing;
            this.IsNamespaced = isNamespaced;
            this.AggressiveMemoryOptimizations = aggressiveMemoryOptimizations;

            // get asset data
            this.BaseDisposableReferences = reflection.GetField<List<IDisposable>>(this, "disposableAssets").GetValue();
        }

        /// <inheritdoc />
        [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Inherited from base method.")]
        public sealed override bool DoesAssetExist<T>(string localized_asset_name)
        {
            if (string.IsNullOrWhiteSpace(localized_asset_name))
                return false;

            IAssetName assetName = this.Coordinator.ParseAssetName(this.PrenormalizeRawAssetName(localized_asset_name), allowLocales: this.TryLocalizeKeys);
            return this.DoesAssetExist<T>(assetName);
        }

        /// <inheritdoc />
        public virtual bool DoesAssetExist<T>(IAssetName assetName)
        {
            if (this.CheckGameFolderForAssetExists && base.DoesAssetExist<T>(assetName.Name))
                return true;

            return this.Cache.ContainsKey(assetName.Name);
        }

        /// <inheritdoc />
        [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Inherited from base method.")]
        public sealed override T LoadImpl<T>(string base_asset_name, string localized_asset_name, LanguageCode language_code)
        {
            IAssetName assetName = this.Coordinator.ParseAssetName(this.PrenormalizeRawAssetName(localized_asset_name), allowLocales: this.TryLocalizeKeys);
            return this.LoadExact<T>(assetName, useCache: true);
        }

        /// <inheritdoc />
        public T LoadLocalized<T>(IAssetName assetName, LanguageCode language, bool useCache)
        {
            // ignore locale in English (or if disabled)
            if (!this.TryLocalizeKeys || language == LocalizedContentManager.LanguageCode.en)
                return this.LoadExact<T>(assetName, useCache: useCache);

            // check for localized asset
            if (!LocalizedContentManager.localizedAssetNames.TryGetValue(assetName.Name, out _))
            {
                string localeCode = this.LanguageCodeString(language);
                IAssetName localizedName = new AssetName(baseName: assetName.BaseName, localeCode: localeCode, languageCode: language);

                try
                {
                    T data = this.LoadExact<T>(localizedName, useCache: useCache);
                    LocalizedContentManager.localizedAssetNames[assetName.Name] = localizedName.Name;
                    return data;
                }
                catch (ContentLoadException)
                {
                    localizedName = new AssetName(assetName.BaseName + "_international", null, null);
                    try
                    {
                        T data = this.LoadExact<T>(localizedName, useCache: useCache);
                        LocalizedContentManager.localizedAssetNames[assetName.Name] = localizedName.Name;
                        return data;
                    }
                    catch (ContentLoadException)
                    {
                        LocalizedContentManager.localizedAssetNames[assetName.Name] = assetName.Name;
                    }
                }
            }

            // use cached key
            string rawName = LocalizedContentManager.localizedAssetNames[assetName.Name];
            if (assetName.Name != rawName)
                assetName = this.Coordinator.ParseAssetName(rawName, allowLocales: this.TryLocalizeKeys);
            return this.LoadExact<T>(assetName, useCache: useCache);
        }

        /// <inheritdoc />
        public abstract T LoadExact<T>(IAssetName assetName, bool useCache);

        /// <inheritdoc />
        [SuppressMessage("ReSharper", "ParameterOnlyUsedForPreconditionCheck.Local", Justification = "Parameter is only used for assertion checks by design.")]
        public string AssertAndNormalizeAssetName(string assetName)
        {
            // NOTE: the game checks for ContentLoadException to handle invalid keys, so avoid
            // throwing other types like ArgumentException here.
            if (string.IsNullOrWhiteSpace(assetName))
                throw new SContentLoadException("The asset key or local path is empty.");
            if (assetName.Intersect(Path.GetInvalidPathChars()).Any())
                throw new SContentLoadException("The asset key or local path contains invalid characters.");

            return this.Cache.NormalizeKey(assetName);
        }

        /****
        ** Content loading
        ****/
        /// <inheritdoc />
        public string GetLocale()
        {
            return this.GetLocale(this.GetCurrentLanguage());
        }

        /// <inheritdoc />
        public string GetLocale(LanguageCode language)
        {
            return this.LanguageCodeString(language);
        }

        /// <inheritdoc />
        public bool IsLoaded(IAssetName assetName)
        {
            return this.Cache.ContainsKey(assetName.Name);
        }


        /****
        ** Cache invalidation
        ****/
        /// <inheritdoc />
        public IEnumerable<KeyValuePair<string, object>> GetCachedAssets()
        {
            foreach (string key in this.Cache.Keys)
                yield return new(key, this.Cache[key]);
        }

        /// <summary>Remove an asset from the cache.</summary>
        /// <param name="assetName">The asset name to remove.</param>
        /// <param name="dispose">Whether to dispose invalidated assets. This should only be <c>true</c> when they're being invalidated as part of a dispose, to avoid crashing the game.</param>
        /// <returns>Returns whether the asset was in the cache.</returns>
        public bool InvalidateCache(IAssetName assetName, bool dispose = false)
        {
            if (!this.Cache.ContainsKey(assetName.Name))
                return false;

            // dispose tilesheets
            if (this.AggressiveMemoryOptimizations)
            {
                if (this.Cache[assetName.Name] is Map map)
                    map.DisposeTileSheets(Game1.mapDisplayDevice);
            }

            // remove from cache
            this.Cache.Remove(assetName.Name, dispose);

            return true;
        }

        /// <inheritdoc />
        protected override void Dispose(bool isDisposing)
        {
            // ignore if disposed
            if (this.IsDisposed)
                return;
            this.IsDisposed = true;

            // dispose uncached assets
            foreach (WeakReference<IDisposable> reference in this.Disposables)
            {
                if (reference.TryGetTarget(out IDisposable disposable))
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch { /* ignore dispose errors */ }
                }
            }
            this.Disposables.Clear();

            // raise event
            this.OnDisposing(this);

            base.Dispose(isDisposing);
        }

        /// <inheritdoc />
        public override void Unload()
        {
            if (this.IsDisposed)
                return; // base logic doesn't allow unloading twice, which happens due to SMAPI and the game both unloading

            base.Unload();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Apply initial normalization to a raw asset name before it's parsed.</summary>
        /// <param name="assetName">The asset name to normalize.</param>
        private string PrenormalizeRawAssetName(string assetName)
        {
            // trim
            assetName = assetName?.Trim();

            // For legacy reasons, mods can pass .xnb file extensions to the content pipeline which
            // are then stripped. This will be re-added as needed when reading from raw files.
            if (assetName?.EndsWith(".xnb") == true)
                assetName = assetName[..^".xnb".Length];

            return assetName;
        }

        /// <summary>Normalize path separators in a file path. For asset keys, see <see cref="AssertAndNormalizeAssetName"/> instead.</summary>
        /// <param name="path">The file path to normalize.</param>
        [Pure]
        protected string NormalizePathSeparators(string path)
        {
            return this.Cache.NormalizePathSeparators(path);
        }

        /// <summary>Load an asset file directly from the underlying content manager.</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The normalized asset key.</param>
        /// <param name="useCache">Whether to read/write the loaded asset to the asset cache.</param>
        protected virtual T RawLoad<T>(IAssetName assetName, bool useCache)
        {
            if (useCache)
            {
                if (!this.BaseLoadProxyCache.TryGetValue(typeof(T), out object cacheEntry))
                {
                    MethodInfo method = typeof(ContentManager).GetMethod(nameof(ContentManager.Load)) ?? throw new InvalidOperationException($"Can't get required method '{nameof(ContentManager)}.{nameof(ContentManager.Load)}'.");
                    method = method.MakeGenericMethod(typeof(T));
                    IntPtr pointer = method.MethodHandle.GetFunctionPointer();
                    this.BaseLoadProxyCache[typeof(T)] = cacheEntry = Activator.CreateInstance(typeof(Func<string, T>), this, pointer) ?? throw new InvalidOperationException($"Can't proxy required method '{nameof(ContentManager)}.{nameof(ContentManager.Load)}'.");
                }

                Func<string, T> baseLoad = (Func<string, T>)cacheEntry;

                return baseLoad(assetName.Name);
            }

            return this.ReadAsset<T>(assetName.Name, disposable => this.Disposables.Add(new WeakReference<IDisposable>(disposable)));
        }

        /// <summary>Add tracking data to an asset and add it to the cache.</summary>
        /// <typeparam name="T">The type of asset to inject.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        /// <param name="value">The asset value.</param>
        /// <param name="useCache">Whether to save the asset to the asset cache.</param>
        protected virtual void TrackAsset<T>(IAssetName assetName, T value, bool useCache)
        {
            // track asset key
            if (value is Texture2D texture)
                texture.Name = assetName.Name;

            // save to cache
            // Note: even if the asset was loaded and cached right before this method was called,
            // we need to fully re-inject it because a mod editor may have changed the asset in a
            // way that doesn't change the instance stored in the cache, e.g. using
            // `asset.ReplaceWith`.
            if (useCache)
                this.Cache[assetName.Name] = value;

            // avoid hard disposable references; see remarks on the field
            this.BaseDisposableReferences.Clear();
        }
    }
}
