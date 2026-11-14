using System;
using Unity.Netcode;

/// <summary>
/// foreach over a NetworkList without garbage: NetworkList's own enumerator is handed out boxed, so
/// every foreach over one allocates. Write foreach (var x in list.Each()) instead; it reads by index.
/// </summary>
public static class NetworkListEach
{
    public static View<T> Each<T>(this NetworkList<T> list) where T : unmanaged, IEquatable<T> => new(list);

    public readonly struct View<T> where T : unmanaged, IEquatable<T>
    {
        private readonly NetworkList<T> list;
        public View(NetworkList<T> list) => this.list = list;
        public Enumerator<T> GetEnumerator() => new(list);
    }

    public struct Enumerator<T> where T : unmanaged, IEquatable<T>
    {
        private readonly NetworkList<T> list;
        private int index;

        public Enumerator(NetworkList<T> list)
        {
            this.list = list;
            index = -1;
        }

        public bool MoveNext() => list != null && ++index < list.Count;
        public T Current => list[index];
    }
}
