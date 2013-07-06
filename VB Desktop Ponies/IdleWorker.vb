﻿Imports System.Threading

''' <summary>
''' Processes UI dependant tasks on the UI thread when the application is idle.
''' </summary>
''' <remarks>When the application exits, any tasks queued for execution are abandoned.</remarks>
Public Class IdleWorker

    ''' <summary>
    ''' The idle worker for this thread.
    ''' </summary>
    <ThreadStatic>
    Private Shared worker As IdleWorker

    ''' <summary>
    ''' Gets the idle worker for this thread.
    ''' </summary>
    Public Shared ReadOnly Property CurrentThreadWorker As IdleWorker
        Get
            If worker Is Nothing Then worker = New IdleWorker()
            Return worker
        End Get
    End Property

    ''' <summary>
    ''' A method that does nothing.
    ''' </summary>
    Private Shared ReadOnly DummyCallback As New MethodInvoker(Sub()
                                                               End Sub)

    ''' <summary>
    ''' Maintains a collection of tasks to perform when the application is idle.
    ''' </summary>
    Private ReadOnly tasks As New Queue(Of MethodInvoker)
    ''' <summary>
    ''' Indicates when the queue of tasks to perform is empty.
    ''' </summary>
    Private ReadOnly empty As New Threading.ManualResetEvent(True)
    ''' <summary>
    ''' A control, from which the UI thread can be invoked.
    ''' </summary>
    Private ReadOnly control As Control
    ''' <summary>
    ''' The thread that owns this instance.
    ''' </summary>
    Private ReadOnly owningThread As Threading.Thread

    ''' <summary>
    ''' Indicates if we have disposed of the instance.
    ''' </summary>
    Private disposed As Boolean
    ''' <summary>
    ''' Async result returned from dummy callback, held so wait handle may be disposed.
    ''' </summary>
    Private asyncResult As IAsyncResult

    ''' <summary>
    ''' Initializes a new instance of the <see cref="IdleWorker"/> class on the current thread.
    ''' </summary>
    Private Sub New()
        If Not Application.MessageLoop Then Throw New InvalidOperationException(
            String.Format("A message loop must be running on this thread before the {0} can be accessed.", GetType(IdleWorker).Name))
        owningThread = Threading.Thread.CurrentThread
        control = New Control()
        control.CreateControl()
        AddHandler Application.Idle, AddressOf RunTask
        AddHandler Application.ThreadExit, AddressOf DisposeWorker
    End Sub

    ''' <summary>
    ''' Queues a task for execution when the UI thread is next idle.
    ''' </summary>
    ''' <param name="task">The task which will be executed once other queued tasks have been processed and the UI thread is idle.</param>
    Public Sub QueueTask(task As MethodInvoker)
        Argument.EnsureNotNull(task, "task")
        SyncLock tasks
            ' If the control is disposed or the handle has been lost, then the message pump on this thread has been shut down. We will drop
            ' all new tasks since they can't be processed anyway.
            If control.IsDisposed OrElse Not control.IsHandleCreated Then Return

            Try
                If OperatingSystemInfo.IsWindows Then
                    tasks.Enqueue(task)
                    ' If there were previously no tasks in the queue, the application may already be an an idle state.
                    ' We will post a dummy event to the message queue, so that the idle event can be raised once the message queue is cleared.
                    If tasks.Count = 1 Then
                        asyncResult = control.BeginInvoke(DummyCallback)
                        empty.Reset()
                    End If
                Else
                    ' Mono does not handle the idle event in the same way. Instead we'll just lump the request onto the message queue. This is
                    ' means the caller is still not blocked, but that user interaction will be delayed behind queued tasks.  This becomes an issue
                    ' if a lot of tasks are added under Mono, since they must complete before the UI becomes responsive again.
                    control.BeginInvoke(task)
                End If
            Catch ex As InvalidOperationException
                ' If the handle was lost after our initial check, it means the message pump was closed from another thread.
                ' Again, we will just drop any new tasks.
            End Try
        End SyncLock
    End Sub

    ''' <summary>
    ''' Dequeues and invokes a task on the UI thread.
    ''' </summary>
    ''' <param name="sender">The source of the event.</param>
    ''' <param name="e">Data about the event.</param>
    Private Sub RunTask(sender As Object, e As EventArgs)
        SyncLock tasks
            If Not disposed AndAlso tasks.Count > 0 Then
                tasks.Dequeue().Invoke()
                If tasks.Count = 0 Then
                    empty.Set()
                    If asyncResult IsNot Nothing Then
                        Try
                            control.EndInvoke(asyncResult)
                        Finally
                            asyncResult.AsyncWaitHandle.Dispose()
                            asyncResult = Nothing
                        End Try
                    End If
                End If
            End If
        End SyncLock
    End Sub

    ''' <summary>
    ''' Waits until all tasks queued by this worker have been processed.
    ''' </summary>
    Public Sub WaitOnAllTasks()
        SyncLock tasks
            If disposed Then Return
        End SyncLock
        Try
            empty.WaitOne()
        Catch ex As ObjectDisposedException
            ' This object will be disposed if the UI thread was closed down, in which case we won't be processing events anyway.
        End Try
    End Sub

    ''' <summary>
    ''' Disposes of the worker, if the current thread is exiting.
    ''' </summary>
    ''' <param name="sender">The source of the event.</param>
    ''' <param name="e">Data about the event.</param>
    Private Sub DisposeWorker(sender As Object, e As EventArgs)
        SyncLock tasks
            If Object.ReferenceEquals(owningThread, Threading.Thread.CurrentThread) Then
                disposed = True
                RemoveHandler Application.ThreadExit, AddressOf DisposeWorker
                RemoveHandler Application.Idle, AddressOf RunTask
                empty.Set()
                empty.Dispose()
                control.SmartInvoke(AddressOf control.Dispose)
                If asyncResult IsNot Nothing Then asyncResult.AsyncWaitHandle.Dispose()
                worker = Nothing
            End If
        End SyncLock
    End Sub
End Class