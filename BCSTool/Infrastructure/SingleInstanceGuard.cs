using System;
using System.Threading;

namespace BCSTool.Infrastructure;

/// <summary>
/// Prevents more than one copy of BCS Tool from running.
///
/// A named Mutex is a Windows synchronization primitive. The first process
/// that creates the named mutex "owns" it. A second BCS Tool process sees
/// that the mutex already exists and exits.
///
/// This is important for a server manager because two watchdogs could both
/// send commands or attempt a restart at the same time.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _name;
    private Mutex? _mutex;
    private bool _ownsMutex;

    public SingleInstanceGuard(string name)
    {
        _name = name;
    }

    /// <summary>
    /// Attempts to become the single active BCS Tool instance.
    /// Returns true only for the first process.
    /// </summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(true, _name, out var createdNew);
        _ownsMutex = createdNew;
        return createdNew;
    }

    /// <summary>
    /// Releases the mutex if this process owns it.
    /// </summary>
    public void Dispose()
    {
        if (_mutex is null)
            return;

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
