using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using DotNext.Net.Cluster;
using Microsoft.Win32.SafeHandles;

namespace TerraBase.Cluster;

internal readonly record struct RaftLogEntryData(long Term, int? CommandId, bool IsConfiguration, ReadOnlyMemory<byte> Payload);

internal readonly record struct FollowerAppendResult(bool Matched, long LastIndex);

internal sealed class RaftShardLog(string location, long snapshotThreshold, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> apply) : IAsyncDisposable
{
    private readonly string _walPath = Path.Combine(location, "raft.wal");
    private readonly string _compactPath = Path.Combine(location, "raft.wal.compact");
    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _commitLock = new(1, 1);
    private readonly List<RaftLogEntryData> _entries = [];

    private SafeFileHandle? _handle;
    private long _writeOffset;
    private long _term;
    private bool _hasVotedFor;
    private ClusterMemberId _votedFor;
    private long _snapshotIndex;
    private long _snapshotTerm;
    private long _lastEntryIndex;
    private long _flushedIndex;
    private long _commitIndex;
    private long _appliedIndex;
    private bool _disposed;

    public long Term => Volatile.Read(ref _term);

    public long LastEntryIndex => Volatile.Read(ref _lastEntryIndex);

    public long FlushedIndex => Volatile.Read(ref _flushedIndex);

    public long SnapshotIndex => Volatile.Read(ref _snapshotIndex);

    public long SnapshotTerm => Volatile.Read(ref _snapshotTerm);

    public long CommitIndex => Volatile.Read(ref _commitIndex);

    public long LastLogTerm
    {
        get
        {
            lock (_stateGate)
            {
                return _entries.Count > 0 ? _entries[^1].Term : _snapshotTerm;
            }
        }
    }

    public void Initialize()
    {
        var handle = RaftWalCodec.OpenWalFile(_walPath);
        _handle = handle;
        var length = RandomAccess.GetLength(handle);
        if (length < RaftWalCodec.FileHeaderSize)
        {
            RaftWalCodec.WriteFileHeader(handle);
            _writeOffset = RaftWalCodec.FileHeaderSize;
            return;
        }

        _writeOffset = ReplayWal(handle, length);
        Volatile.Write(ref _flushedIndex, _lastEntryIndex);
    }

    public bool CanVoteFor(ClusterMemberId candidate)
    {
        lock (_stateGate)
        {
            return !_hasVotedFor || _votedFor == candidate;
        }
    }

    public bool IsUpToDate(long candidateLastLogIndex, long candidateLastLogTerm)
    {
        lock (_stateGate)
        {
            var localLastTerm = _entries.Count > 0 ? _entries[^1].Term : _snapshotTerm;
            var localLastIndex = _lastEntryIndex;
            return candidateLastLogTerm > localLastTerm
                || (candidateLastLogTerm == localLastTerm && candidateLastLogIndex >= localLastIndex);
        }
    }

    public long TermAt(long logIndex)
    {
        lock (_stateGate)
        {
            return TermAtLocked(logIndex);
        }
    }

    public (long LastIndex, long LastTerm) LastIndexAndTerm()
    {
        lock (_stateGate)
        {
            var term = _entries.Count > 0 ? _entries[^1].Term : _snapshotTerm;
            return (_lastEntryIndex, term);
        }
    }

    public async ValueTask<long> IncrementTermAndVoteSelfAsync(ClusterMemberId self, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            long nextTerm;
            lock (_stateGate)
            {
                nextTerm = _term + 1;
                Volatile.Write(ref _term, nextTerm);
                _hasVotedFor = true;
                _votedFor = self;
            }

            PersistTermVote(nextTerm, true, self);
            return nextTerm;
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    public async ValueTask UpdateTermAsync(long term, bool resetVote, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            bool hasVote;
            ClusterMemberId votedFor;
            lock (_stateGate)
            {
                if (term < _term)
                {
                    return;
                }

                if (term == _term && (!resetVote || !_hasVotedFor))
                {
                    return;
                }

                Volatile.Write(ref _term, term);
                if (resetVote)
                {
                    _hasVotedFor = false;
                    _votedFor = default;
                }

                hasVote = _hasVotedFor;
                votedFor = _votedFor;
            }

            PersistTermVote(term, hasVote, votedFor);
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    public async ValueTask<bool> TryGrantVoteAsync(long term, ClusterMemberId candidate, long candidateLastIndex, long candidateLastTerm, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            lock (_stateGate)
            {
                if (term < _term)
                {
                    return false;
                }

                if (term > _term)
                {
                    Volatile.Write(ref _term, term);
                    _hasVotedFor = false;
                    _votedFor = default;
                }

                var localLastTerm = _entries.Count > 0 ? _entries[^1].Term : _snapshotTerm;
                var upToDate = candidateLastTerm > localLastTerm
                    || (candidateLastTerm == localLastTerm && candidateLastIndex >= _lastEntryIndex);
                if (!upToDate || (_hasVotedFor && _votedFor != candidate))
                {
                    PersistTermVote(_term, _hasVotedFor, _votedFor);
                    return false;
                }

                _hasVotedFor = true;
                _votedFor = candidate;
            }

            PersistTermVote(term, true, candidate);
            return true;
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    public long AppendLocalMemory(long term, ReadOnlyMemory<byte> payload)
    {
        lock (_stateGate)
        {
            var nextIndex = _lastEntryIndex + 1;
            _entries.Add(new RaftLogEntryData(term, null, false, payload));
            Volatile.Write(ref _lastEntryIndex, nextIndex);
            return nextIndex;
        }
    }

    public async ValueTask<long> FlushAppendedAsync(long targetIndex, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _flushedIndex) >= targetIndex)
        {
            return Volatile.Read(ref _flushedIndex);
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var alreadyFlushed = Volatile.Read(ref _flushedIndex);
            if (alreadyFlushed >= targetIndex)
            {
                return alreadyFlushed;
            }

            long firstToWrite;
            RaftLogEntryData single = default;
            RaftLogEntryData[]? batch = null;
            int count;
            lock (_stateGate)
            {
                var currentLast = _lastEntryIndex;
                if (alreadyFlushed >= currentLast)
                {
                    return alreadyFlushed;
                }

                firstToWrite = Math.Max(alreadyFlushed + 1, _snapshotIndex + 1);
                count = (int)(currentLast - firstToWrite + 1);
                if (count <= 0)
                {
                    return alreadyFlushed;
                }

                if (count == 1)
                {
                    single = EntryAtLocked(firstToWrite);
                }
                else
                {
                    batch = new RaftLogEntryData[count];
                    for (var index = 0; index < count; index++)
                    {
                        batch[index] = EntryAtLocked(firstToWrite + index);
                    }
                }
            }

            if (batch is null)
            {
                PersistSingleAppend(firstToWrite, single);
            }
            else
            {
                PersistArrayBatch(false, 0, firstToWrite, batch);
            }

            var newFlushed = firstToWrite + count - 1;
            Volatile.Write(ref _flushedIndex, newFlushed);
            return newFlushed;
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    public bool TryReadForPeer(long nextIndex, int maxEntries, out long prevLogIndex, out long prevLogTerm, out IReadOnlyList<RaftWireEntry> entries)
    {
        lock (_stateGate)
        {
            if (nextIndex <= _snapshotIndex)
            {
                prevLogIndex = _snapshotIndex;
                prevLogTerm = _snapshotTerm;
                entries = [];
                return false;
            }

            prevLogIndex = nextIndex - 1;
            prevLogTerm = TermAtLocked(prevLogIndex);
            if (nextIndex > _lastEntryIndex)
            {
                entries = [];
                return true;
            }

            var available = (int)Math.Min(_lastEntryIndex - nextIndex + 1, maxEntries);
            var wire = new RaftWireEntry[available];
            for (var index = 0; index < available; index++)
            {
                var entry = EntryAtLocked(nextIndex + index);
                wire[index] = new RaftWireEntry(entry.Term, entry.CommandId, entry.IsConfiguration, entry.Payload);
            }

            entries = wire;
            return true;
        }
    }

    public async ValueTask<FollowerAppendResult> TryAppendFromLeaderAsync(long prevLogIndex, long prevLogTerm, IReadOnlyList<RaftWireEntry> entries, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            int startOffset = 0;
            long appendStartIndex;
            bool needTruncate;
            long truncateTo;
            lock (_stateGate)
            {
                if (prevLogIndex > _lastEntryIndex)
                {
                    return new FollowerAppendResult(false, _lastEntryIndex);
                }

                if (prevLogIndex > _snapshotIndex)
                {
                    if (TermAtLocked(prevLogIndex) != prevLogTerm)
                    {
                        var conflictTerm = TermAtLocked(prevLogIndex);
                        var rewind = prevLogIndex - 1;
                        while (rewind > _snapshotIndex && TermAtLocked(rewind) == conflictTerm)
                        {
                            rewind--;
                        }

                        return new FollowerAppendResult(false, Math.Max(_snapshotIndex, rewind));
                    }
                }
                else if (prevLogIndex == _snapshotIndex && prevLogTerm != _snapshotTerm)
                {
                    return new FollowerAppendResult(false, _snapshotIndex);
                }

                if (entries.Count == 0)
                {
                    return new FollowerAppendResult(true, _lastEntryIndex);
                }

                var currentIndex = prevLogIndex + 1;
                while (startOffset < entries.Count && currentIndex <= _snapshotIndex)
                {
                    startOffset++;
                    currentIndex++;
                }

                while (startOffset < entries.Count && currentIndex <= _lastEntryIndex)
                {
                    if (EntryAtLocked(currentIndex).Term != entries[startOffset].Term)
                    {
                        break;
                    }

                    startOffset++;
                    currentIndex++;
                }

                if (startOffset >= entries.Count)
                {
                    return new FollowerAppendResult(true, _lastEntryIndex);
                }

                appendStartIndex = currentIndex;
                needTruncate = currentIndex <= _lastEntryIndex;
                truncateTo = currentIndex - 1;
            }

            var count = entries.Count - startOffset;
            if (count == 1 && !needTruncate)
            {
                var wire = entries[startOffset];
                var singleEntry = new RaftLogEntryData(wire.Term, wire.CommandId, wire.IsConfiguration, wire.Payload);
                PersistSingleAppend(appendStartIndex, singleEntry);
                lock (_stateGate)
                {
                    _entries.Add(singleEntry);
                    Volatile.Write(ref _lastEntryIndex, appendStartIndex);
                    Volatile.Write(ref _flushedIndex, appendStartIndex);
                }

                return new FollowerAppendResult(true, appendStartIndex);
            }

            var toWrite = new RaftLogEntryData[count];
            for (var index = 0; index < count; index++)
            {
                var wire = entries[startOffset + index];
                toWrite[index] = new RaftLogEntryData(wire.Term, wire.CommandId, wire.IsConfiguration, wire.Payload);
            }

            PersistArrayBatch(needTruncate, truncateTo, appendStartIndex, toWrite);
            long updatedLast;
            lock (_stateGate)
            {
                if (needTruncate)
                {
                    TruncateLocked(truncateTo);
                }

                for (var index = 0; index < toWrite.Length; index++)
                {
                    _entries.Add(toWrite[index]);
                }

                updatedLast = appendStartIndex + toWrite.Length - 1;
                Volatile.Write(ref _lastEntryIndex, updatedLast);
                Volatile.Write(ref _flushedIndex, updatedLast);
            }

            return new FollowerAppendResult(true, updatedLast);
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    public async ValueTask InstallSnapshotAsync(long snapshotIndex, long snapshotTerm, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (snapshotIndex <= Volatile.Read(ref _snapshotIndex))
            {
                return;
            }

            PersistSnapshot(snapshotIndex, snapshotTerm);
            lock (_stateGate)
            {
                _snapshotIndex = snapshotIndex;
                _snapshotTerm = snapshotTerm;
                _entries.Clear();
                if (_lastEntryIndex < snapshotIndex)
                {
                    Volatile.Write(ref _lastEntryIndex, snapshotIndex);
                }

                if (_flushedIndex < snapshotIndex)
                {
                    Volatile.Write(ref _flushedIndex, snapshotIndex);
                }

                if (_commitIndex < snapshotIndex)
                {
                    Volatile.Write(ref _commitIndex, snapshotIndex);
                }

                if (_appliedIndex < snapshotIndex)
                {
                    Volatile.Write(ref _appliedIndex, snapshotIndex);
                }
            }
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    public async ValueTask ApplyUpToAsync(long targetCommitIndex, CancellationToken cancellationToken)
    {
        await _commitLock.WaitAsync(cancellationToken);
        try
        {
            var flushed = Volatile.Read(ref _flushedIndex);
            var target = Math.Min(targetCommitIndex, flushed);
            var applied = Volatile.Read(ref _appliedIndex);
            if (target <= applied)
            {
                if (target > Volatile.Read(ref _commitIndex))
                {
                    Volatile.Write(ref _commitIndex, target);
                }

                return;
            }

            var count = (int)(target - applied);
            RaftLogEntryData single = default;
            RaftLogEntryData[]? batch = null;
            lock (_stateGate)
            {
                if (target > _commitIndex)
                {
                    Volatile.Write(ref _commitIndex, target);
                }

                var start = Math.Max(applied + 1, _snapshotIndex + 1);
                if (start > target)
                {
                    Volatile.Write(ref _appliedIndex, target);
                    return;
                }

                count = (int)(target - start + 1);
                if (count == 1)
                {
                    single = EntryAtLocked(target);
                }
                else
                {
                    batch = new RaftLogEntryData[count];
                    for (var index = 0; index < count; index++)
                    {
                        batch[index] = EntryAtLocked(start + index);
                    }
                }
            }

            if (batch is null)
            {
                if (!single.IsConfiguration && !single.Payload.IsEmpty)
                {
                    await apply(single.Payload, cancellationToken);
                }
            }
            else
            {
                for (var index = 0; index < batch.Length; index++)
                {
                    var entry = batch[index];
                    if (!entry.IsConfiguration && !entry.Payload.IsEmpty)
                    {
                        await apply(entry.Payload, cancellationToken);
                    }
                }
            }

            Volatile.Write(ref _appliedIndex, target);
            if (target - Volatile.Read(ref _snapshotIndex) >= snapshotThreshold)
            {
                await CompactAsync(target, cancellationToken);
            }
        }
        finally
        {
            _ = _commitLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _commitLock.WaitAsync();
        await _writeLock.WaitAsync();
        try
        {
            _handle?.Dispose();
            _handle = null;
        }
        finally
        {
            _ = _writeLock.Release();
            _ = _commitLock.Release();
            _writeLock.Dispose();
            _commitLock.Dispose();
        }
    }

    private long TermAtLocked(long logIndex)
    {
        if (logIndex <= 0)
        {
            return 0;
        }

        if (logIndex == _snapshotIndex)
        {
            return _snapshotTerm;
        }

        if (logIndex < _snapshotIndex || logIndex > _lastEntryIndex)
        {
            return -1;
        }

        return EntryAtLocked(logIndex).Term;
    }

    private RaftLogEntryData EntryAtLocked(long logIndex) => _entries[(int)(logIndex - _snapshotIndex - 1)];

    private void TruncateLocked(long newLastIndex)
    {
        var keep = (int)Math.Max(0, newLastIndex - _snapshotIndex);
        if (keep < _entries.Count)
        {
            _entries.RemoveRange(keep, _entries.Count - keep);
        }

        var clamped = Math.Max(_snapshotIndex, newLastIndex);
        Volatile.Write(ref _lastEntryIndex, clamped);
        if (_flushedIndex > clamped)
        {
            Volatile.Write(ref _flushedIndex, clamped);
        }
    }

    private async ValueTask CompactAsync(long compactToIndex, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            long term;
            bool hasVote;
            ClusterMemberId votedFor;
            long snapTerm;
            RaftLogEntryData[] remaining;
            lock (_stateGate)
            {
                if (compactToIndex <= _snapshotIndex || compactToIndex > _flushedIndex)
                {
                    return;
                }

                term = _term;
                hasVote = _hasVotedFor;
                votedFor = _votedFor;
                snapTerm = EntryAtLocked(compactToIndex).Term;
                var dropCount = (int)(compactToIndex - _snapshotIndex);
                var flushedRemain = (int)Math.Max(0, _flushedIndex - compactToIndex);
                remaining = new RaftLogEntryData[flushedRemain];
                for (var index = 0; index < flushedRemain; index++)
                {
                    remaining[index] = _entries[dropCount + index];
                }
            }

            var totalBytes = RaftWalCodec.FileHeaderSize
                + RaftWalCodec.RecordFrameSize(9 + (hasVote ? ClusterMemberId.Size : 0))
                + RaftWalCodec.RecordFrameSize(16);
            for (var index = 0; index < remaining.Length; index++)
            {
                totalBytes += RaftWalCodec.RecordFrameSize(21 + remaining[index].Payload.Length);
            }

            var rented = ArrayPool<byte>.Shared.Rent(totalBytes);
            try
            {
                var span = rented.AsSpan(0, totalBytes);
                var offset = RaftWalCodec.WriteHeaderToSpan(span);
                offset += RaftWalCodec.WriteTermVoteRecord(span[offset..], term, hasVote, votedFor);
                offset += RaftWalCodec.WriteSnapshotRecord(span[offset..], compactToIndex, snapTerm);
                for (var index = 0; index < remaining.Length; index++)
                {
                    offset += RaftWalCodec.WriteAppendRecord(span[offset..], compactToIndex + 1 + index, remaining[index]);
                }

                using (var compactHandle = RaftWalCodec.OpenNewWalFile(_compactPath))
                {
                    RandomAccess.Write(compactHandle, span[..offset], 0);
                    RandomAccess.FlushToDisk(compactHandle);
                }

                _handle?.Dispose();
                _handle = null;
                File.Move(_compactPath, _walPath, overwrite: true);
                _handle = RaftWalCodec.OpenWalFile(_walPath);
                _writeOffset = offset;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            lock (_stateGate)
            {
                var dropCount = (int)(compactToIndex - _snapshotIndex);
                if (dropCount > 0 && dropCount <= _entries.Count)
                {
                    _entries.RemoveRange(0, dropCount);
                }

                Volatile.Write(ref _snapshotIndex, compactToIndex);
                Volatile.Write(ref _snapshotTerm, snapTerm);
            }
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    private SafeFileHandle ActiveHandle() => _handle ?? throw new ObjectDisposedException(nameof(RaftShardLog));

    private void PersistTermVote(long term, bool hasVote, ClusterMemberId votedFor)
    {
        var payloadLen = 9 + (hasVote ? ClusterMemberId.Size : 0);
        Span<byte> frame = stackalloc byte[RaftWalCodec.RecordFrameSize(payloadLen)];
        var written = RaftWalCodec.WriteTermVoteRecord(frame, term, hasVote, votedFor);
        var handle = ActiveHandle();
        RandomAccess.Write(handle, frame[..written], _writeOffset);
        _writeOffset += written;
    }

    private void PersistSnapshot(long snapshotIndex, long snapshotTerm)
    {
        Span<byte> frame = stackalloc byte[RaftWalCodec.RecordFrameSize(16)];
        var written = RaftWalCodec.WriteSnapshotRecord(frame, snapshotIndex, snapshotTerm);
        var handle = ActiveHandle();
        RandomAccess.Write(handle, frame[..written], _writeOffset);
        _writeOffset += written;
    }

    private void PersistSingleAppend(long logIndex, RaftLogEntryData entry)
    {
        var total = RaftWalCodec.RecordFrameSize(21 + entry.Payload.Length);
        if (total <= 1024)
        {
            Span<byte> frame = stackalloc byte[total];
            var written = RaftWalCodec.WriteAppendRecord(frame, logIndex, entry);
            var handle = ActiveHandle();
            RandomAccess.Write(handle, frame[..written], _writeOffset);
            _writeOffset += written;
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var written = RaftWalCodec.WriteAppendRecord(rented, logIndex, entry);
            var handle = ActiveHandle();
            RandomAccess.Write(handle, rented.AsSpan(0, written), _writeOffset);
            _writeOffset += written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void PersistArrayBatch(bool needTruncate, long truncateTo, long startIndex, RaftLogEntryData[] entries)
    {
        var total = needTruncate ? RaftWalCodec.RecordFrameSize(8) : 0;
        for (var index = 0; index < entries.Length; index++)
        {
            total += RaftWalCodec.RecordFrameSize(21 + entries[index].Payload.Length);
        }

        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var span = rented.AsSpan(0, total);
            var offset = 0;
            if (needTruncate)
            {
                offset += RaftWalCodec.WriteTruncateRecord(span[offset..], truncateTo);
            }

            for (var index = 0; index < entries.Length; index++)
            {
                offset += RaftWalCodec.WriteAppendRecord(span[offset..], startIndex + index, entries[index]);
            }

            var handle = ActiveHandle();
            RandomAccess.Write(handle, span[..offset], _writeOffset);
            _writeOffset += offset;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private long ReplayWal(SafeFileHandle handle, long fileLength)
    {
        var data = new byte[fileLength];
        var readTotal = 0;
        while (readTotal < data.Length)
        {
            var read = RandomAccess.Read(handle, data.AsSpan(readTotal), readTotal);
            if (read == 0)
            {
                break;
            }

            readTotal += read;
        }

        ReadOnlySpan<byte> span = data.AsSpan(0, readTotal);
        if (span.Length < RaftWalCodec.FileHeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(span) != RaftWalCodec.Magic
            || BinaryPrimitives.ReadInt32LittleEndian(span[4..]) != RaftWalCodec.FormatVersion)
        {
            RandomAccess.SetLength(handle, 0);
            RaftWalCodec.WriteFileHeader(handle);
            return RaftWalCodec.FileHeaderSize;
        }

        var pos = RaftWalCodec.FileHeaderSize;
        while (pos + RaftWalCodec.RecordHeaderSize + RaftWalCodec.ChecksumSize <= span.Length)
        {
            var kind = span[pos];
            var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(span[(pos + 1)..]);
            if (payloadLen < 0 || pos + RaftWalCodec.RecordHeaderSize + payloadLen + RaftWalCodec.ChecksumSize > span.Length)
            {
                break;
            }

            var bodySpan = span.Slice(pos, RaftWalCodec.RecordHeaderSize + payloadLen);
            var storedHash = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + RaftWalCodec.RecordHeaderSize + payloadLen)..]);
            if (XxHash32.HashToUInt32(bodySpan) != storedHash)
            {
                break;
            }

            ApplyReplayedRecord(kind, span.Slice(pos + RaftWalCodec.RecordHeaderSize, payloadLen));
            pos += RaftWalCodec.RecordHeaderSize + payloadLen + RaftWalCodec.ChecksumSize;
        }

        if (pos < fileLength)
        {
            RandomAccess.SetLength(handle, pos);
            RandomAccess.FlushToDisk(handle);
        }

        return pos;
    }

    private void ApplyReplayedRecord(byte kind, ReadOnlySpan<byte> payload)
    {
        switch (kind)
        {
            case RaftWalCodec.RecordTermVote when payload.Length >= 9:
                Volatile.Write(ref _term, BinaryPrimitives.ReadInt64LittleEndian(payload));
                _hasVotedFor = payload[8] == 1;
                _votedFor = _hasVotedFor && payload.Length >= 9 + ClusterMemberId.Size
                    ? new ClusterMemberId(payload.Slice(9, ClusterMemberId.Size))
                    : default;
                break;
            case RaftWalCodec.RecordAppend when payload.Length >= 21:
                var index = BinaryPrimitives.ReadInt64LittleEndian(payload);
                var term = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
                var flags = payload[16];
                var commandId = BinaryPrimitives.ReadInt32LittleEndian(payload[17..]);
                var entryPayload = payload.Length > 21 ? payload[21..].ToArray() : [];
                var entry = new RaftLogEntryData(term, (flags & 1) != 0 ? commandId : null, (flags & 2) != 0, entryPayload);
                if (index > _snapshotIndex)
                {
                    if (index <= _lastEntryIndex)
                    {
                        TruncateLocked(index - 1);
                    }

                    if (index == _lastEntryIndex + 1)
                    {
                        _entries.Add(entry);
                        Volatile.Write(ref _lastEntryIndex, index);
                    }
                }

                break;
            case RaftWalCodec.RecordTruncate when payload.Length >= 8:
                TruncateLocked(BinaryPrimitives.ReadInt64LittleEndian(payload));
                break;
            case RaftWalCodec.RecordSnapshot when payload.Length >= 16:
                var snapIndex = BinaryPrimitives.ReadInt64LittleEndian(payload);
                var snapTerm = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
                Volatile.Write(ref _snapshotIndex, snapIndex);
                Volatile.Write(ref _snapshotTerm, snapTerm);
                _entries.Clear();
                Volatile.Write(ref _lastEntryIndex, Math.Max(_lastEntryIndex, snapIndex));
                Volatile.Write(ref _commitIndex, Math.Max(_commitIndex, snapIndex));
                Volatile.Write(ref _appliedIndex, Math.Max(_appliedIndex, snapIndex));
                break;
            default:
                break;
        }
    }
}
