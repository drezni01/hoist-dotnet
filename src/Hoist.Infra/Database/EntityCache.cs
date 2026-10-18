using System.Collections.Concurrent;
using System.Reflection;

namespace Hoist.Infra.Database
{
    /// <summary>
    /// An in-memory copy of one table (or one slice of it), loaded from the database on first read and kept
    /// current by the caching repositories, which write to the database first and then to this cache.
    ///
    /// Entities are copied on the way in and on the way out. Services load an entity, change it, then
    /// validate and save it, so handing out the cached instance would leak edits that fail validation.
    /// </summary>
    public class EntityCache<TKey, T> where T : class
    {
        private readonly Func<Task<List<T>>> loader;
        private readonly Func<T, TKey> keyOf;
        private readonly Func<T, T> copy;
        private readonly IEqualityComparer<TKey> comparer;
        private readonly SemaphoreSlim loadLock = new SemaphoreSlim(1, 1);
        private readonly object sync = new object();

        private ConcurrentDictionary<TKey, T> entries;

        // Bumped by every write, so a load that read the table before a write committed is discarded.
        private long generation;

        public EntityCache(Func<Task<List<T>>> loader, Func<T, TKey> keyOf, Func<T, T> copy, IEqualityComparer<TKey> comparer = null)
        {
            this.loader = loader;
            this.keyOf = keyOf;
            this.copy = copy;
            this.comparer = comparer ?? EqualityComparer<TKey>.Default;
        }

        public bool IsLoaded => Volatile.Read(ref entries) != null;

        /// <summary>Copies of every cached entity, in no particular order.</summary>
        public async Task<List<T>> ListAsync()
        {
            var all = await EnsureLoadedAsync();
            return all.Values.Select(copy).ToList();
        }

        /// <summary>Copies of the cached entities that match, in no particular order.</summary>
        public async Task<List<T>> WhereAsync(Func<T, bool> predicate)
        {
            var all = await EnsureLoadedAsync();
            return all.Values.Where(predicate).Select(copy).ToList();
        }

        /// <summary>A copy of the first cached entity that matches, or null.</summary>
        public async Task<T> FirstOrDefaultAsync(Func<T, bool> predicate)
        {
            var all = await EnsureLoadedAsync();
            var match = all.Values.FirstOrDefault(predicate);
            return match == null ? null : copy(match);
        }

        public async Task<T> GetAsync(TKey key)
        {
            var all = await EnsureLoadedAsync();
            return all.TryGetValue(key, out var match) ? copy(match) : null;
        }

        /// <summary>Stores a copy of an entity the database has just saved.</summary>
        public void Put(T entity)
        {
            if (entity == null) return;
            var stored = copy(entity);
            lock (sync)
            {
                generation++;
                entries?.AddOrUpdate(keyOf(stored), stored, (_, _) => stored);
            }
        }

        public void Remove(TKey key)
        {
            lock (sync)
            {
                generation++;
                entries?.TryRemove(key, out _);
            }
        }

        public void RemoveWhere(Func<T, bool> predicate)
        {
            lock (sync)
            {
                generation++;
                if (entries == null) return;
                foreach (var entry in entries.Where(e => predicate(e.Value)).ToList()) entries.TryRemove(entry.Key, out _);
            }
        }

        /// <summary>Drops everything; the next read reloads from the database.</summary>
        public void Clear()
        {
            lock (sync)
            {
                generation++;
                entries = null;
            }
        }

        private async Task<ConcurrentDictionary<TKey, T>> EnsureLoadedAsync()
        {
            var current = Volatile.Read(ref entries);
            if (current != null) return current;

            await loadLock.WaitAsync();
            try
            {
                while (true)
                {
                    current = Volatile.Read(ref entries);
                    if (current != null) return current;

                    var startGeneration = Interlocked.Read(ref generation);
                    var rows = await loader();
                    var loaded = new ConcurrentDictionary<TKey, T>(comparer);
                    foreach (var row in rows) loaded[keyOf(row)] = copy(row);

                    lock (sync)
                    {
                        if (generation == startGeneration)
                        {
                            entries = loaded;
                            return loaded;
                        }
                    }
                    // A write landed while loading: the rows may predate it, so read the table again.
                }
            }
            finally
            {
                loadLock.Release();
            }
        }
    }

    /// <summary>Copies of the cached entities. Scalar properties are copied by MemberwiseClone, so new columns need no change here.</summary>
    public static class EntityCopy
    {
        private static readonly Func<object, object> memberwiseClone = (Func<object, object>)typeof(object)
            .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)
            .CreateDelegate(typeof(Func<object, object>));

        public static T Shallow<T>(T entity) where T : class => entity == null ? null : (T)memberwiseClone(entity);
    }
}
