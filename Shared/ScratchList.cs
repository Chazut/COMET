using System;
using System.Collections.Generic;

namespace Comet.Core;

// Main-thread scratch. A nested call borrows a different list until its caller releases one.
internal sealed class ScratchList<T>
{
    private List<T>? available;

    internal Lease Rent()
    {
        var items = available ?? new List<T>();
        available = null;
        return new Lease(this, items);
    }

    internal readonly struct Lease : IDisposable
    {
        private readonly ScratchList<T>? owner;
        internal readonly List<T> Items;

        internal Lease(ScratchList<T> owner, List<T> items)
        {
            this.owner = owner;
            Items = items;
        }

        public void Dispose()
        {
            if (owner == null)
                return;
            Items.Clear();
            if (owner.available == null || owner.available.Capacity < Items.Capacity)
                owner.available = Items;
        }
    }
}
