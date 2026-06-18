namespace libPartclone
{
    //Describes the first contiguous segment that satisfies a read of up to `count` bytes
    //starting at a given output position.
    public readonly struct ContentLocation
    {
        //true  => the bytes live in the image content at ContentOffset
        //false => the bytes are an unpopulated (empty) region and should be zero-filled
        public bool IsPopulated { get; init; }

        //byte offset within the image content stream (valid only when IsPopulated)
        public long ContentOffset { get; init; }

        //number of contiguous bytes available from the requested position (0 => nothing to read)
        public int Length { get; init; }
    }

    public interface IPartcloneContentMap
    {
        ContentLocation Locate(long position, int count);

        //true if every byte from `position` to the end of the partition is unpopulated (null).
        //Used by the sparse-aware reader to stop early when the caller doesn't want trailing nulls.
        bool RestIsAllNullFrom(long position);
    }
}
