using System.Numerics;
using Groundwork.Voxels;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    internal D3D12PropCells? PropStream;
    internal VolumePublicationInfo? PropTerrain;
    internal bool AllowPropUploads = true;
    private ID3D12RootSignature* _propRoot;
    private ID3D12PipelineState* _propPipeline;
    private ID3D12Resource* _propData;
    private ID3D12Resource* _propGeometry;
    private byte* _propMapped;
    private byte* _propGeometryMapped;
    private PackedMesh[]? _propMeshes;
    private VisibleProp[]? _visibleProps;
    private int[]?[] _propResidentPayloads = [];
    private PropUpload[] _propUploads = [];
    private int[] _propFreePayloads = [];
    private int _propFreeCount;
    private int[] _propCandidateBySlot = [];
    private int[] _propPreparedUploads = [];
    private int _propPreparedCount;
    private int _propNextReady;
    private D3D12PropCells? _propFrameStream;
    private ulong _propFrameVersion;
    private bool _propAdmissionPrepared;
    private bool _propReportPendingWorkers;
    private bool _propReportReady;
    private D3D12PropStatistics? _propFrameStatistics;
    private ulong? _propUploadSequence;
    private int _propPayloadWritten;
    private int _propMetadataWritten;
    private PropReference[] _propReferences = [];
    private PropReference[] _propWrittenReferences = [];
    private int _propWrittenReferenceCount;
    private bool _propUnavailable;
    internal Action? PropInitializationForTests;
    private int _propMeshCursor;
    private int _propGeometryOffset;
    private int _nextPropRequest;
    private int _propPayloadCapacity;
    private long _propNativeBytes;

    private readonly record struct PropReference(Vector3 Offset, uint Payload);

    private struct PropUpload(PropCellRequest request, int slot, int[] payload)
    {
        internal readonly PropCellRequest Request = request;
        internal readonly int Slot = slot;
        internal readonly int[] Payload = payload;
        internal int Written;
    }

    internal bool HasReadyPropUploads(in RenderScene scene, in VolumePublicationInfo terrain)
    {
        if (_propUnavailable || PropStream is not { } stream || scene.PropOrigin is not { IsValid: true } ||
            !scene.PropEye.IsValid)
            return false;

        lock (stream.Gate)
        {
            if (!CanDrawProps(stream, terrain))
                return false;
            foreach (var candidate in stream.Ready)
            {
                if (candidate.Request.Generation != 0 &&
                    stream.Admission.ReadStagedLength(candidate.Request, out _, out _) == PropCellResult.Accepted)
                    return true;
            }
            return false;
        }
    }

    private static bool CanDrawProps(D3D12PropCells stream, in VolumePublicationInfo terrain) =>
        !stream.Closed && stream.Publication.RendererEpoch == terrain.View.RendererEpoch &&
        stream.Publication.TerrainRevision == terrain.TerrainRevision &&
        stream.Publication.CoverageGeneration == terrain.Generation;

    private void DrawProps(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderScene scene, ulong sequence)
    {
        if (PropStream is not { } stream || PropTerrain is not { } terrain || scene.PropOrigin is not { } origin)
            return;

        // The window owner only renders after the preceding submission and Present retire.
        // Offscreen callers also wait for retirement before touching this arena again.
        if (!IsIdle)
            return;
        lock (stream.Gate)
        {
            _propFrameStream = stream;
            _propFrameVersion = stream.ViewVersion;
            if (_propUploadSequence != sequence)
            {
                ResetPropUploadBudget();
                _propUploadSequence = sequence;
            }
            if (!CanDrawProps(stream, terrain) || !origin.IsValid || !scene.PropEye.IsValid)
            {
                RecordPropStatistics(0, 0);
                return;
            }

            if (!AllowPropUploads && (_propMeshes is null || _propMeshCursor != _propMeshes.Length))
            {
                _propReportPendingWorkers = true;
                RecordPropStatistics(0, 0);
                return;
            }
            if (!TryInitializeProps(stream))
                return;
            stream.Admission.BeginFrame();
            _propAdmissionPrepared = true;
            if (AllowPropUploads)
                PreparePropUploads(stream);
            else
            {
                // The texture copy spent this turn's payload allowance; already
                // resident props can still be drawn with current frame references.
                _propNextReady = _nextPropRequest;
                Array.Fill(_propCandidateBySlot, -1);
            }
            if (_propMeshCursor != _propMeshes!.Length)
            {
                _propReportPendingWorkers = true;
                RecordPropStatistics(0, 0);
                return;
            }

            var lens = Matrix4x4.CreatePerspectiveFieldOfView(scene.VerticalFieldOfView,
                (float)target.Width / target.Height, scene.NearPlane, scene.FarPlane);
            var projection = scene.View * lens;
            var cameraRelative = scene.View;
            cameraRelative.Translation = Vector3.Zero;
            Span<PropFrustumPlane> planes = stackalloc PropFrustumPlane[6];
            ExtractPropFrustum(cameraRelative * lens, planes);
            foreach (var plane in planes)
            {
                if (plane.IsValid)
                    continue;
                _propReportPendingWorkers = true;
                RecordPropStatistics(0, 0);
                return;
            }
            _propReportReady = true;
            var count = stream.Admission.SelectFrame(scene.PropEye, planes, _visibleProps);
            for (var i = 0; i < count; i++)
            {
                var visible = _visibleProps![i];
                if (!stream.Admission.TryReadFrame(visible, out _, out var point))
                    throw new InvalidOperationException("Prop visibility references a missing frame cell.");
                var delta = point.RelativeTo(origin);
                var candidate = _propCandidateBySlot[visible.CellSlot];
                var payload = candidate >= 0 ? _propUploads[candidate].Payload : _propResidentPayloads[visible.CellSlot]!;
                _propReferences[i] = new(new((float)delta.X, (float)delta.Y, (float)delta.Z),
                    (uint)payload[visible.InstanceIndex]);
            }
            if (!WritePropReferences(count))
            {
                // A changed prop cut can exhaust the upload allowance of a retried
                // sequence. Defer this optional pass so protected terrain can submit.
                stream.Admission.AbortFrame();
                _propAdmissionPrepared = false;
                RecordPropStatistics(0, 0);
                return;
            }

            var draws = 0;
            if (count != 0)
            {
                ObserveUse(target.Buffer);
                ObserveUse(_depth);
                ObserveUse(_propData);
                ObserveUse(_propGeometry);
                var fogDepth = scene.Fog is { MaxOpacity: > 0 } fog ? GetFogDepthConstants(fog) : Vector4.Zero;
                var fogColor = scene.Fog is { } f ? new Vector4(f.Color, f.MaxOpacity) : Vector4.Zero;
                var tint = scene.Tint;
                if (scene.Sunlight is { } sunlight)
                    tint *= new Vector4(sunlight.Ambient + sunlight.Direct, 1);
                var constants = new DrawConstants(projection, tint, fogColor, fogDepth, default, default, default);
                commands->SetPipelineState(_propPipeline);
                commands->SetGraphicsRootSignature(_propRoot);
                commands->SetGraphicsRoot32BitConstants(0, FrameConstantCount, &constants, 0);
                var address = _propData->GetGPUVirtualAddress();
                commands->SetGraphicsRootShaderResourceView(1, address);
                commands->SetGraphicsRootShaderResourceView(2, address + (uint)_propPayloadCapacity);
                for (var start = 0; start < count;)
                {
                    var first = _visibleProps![start];
                    var end = start + 1;
                    while (end < count && SameGroup(first, _visibleProps[end]))
                        end++;
                    var mesh = FindPropMesh(stream, first);
                    var vertices = mesh.Vertices;
                    var indices = mesh.Indices;
                    commands->IASetVertexBuffers(0, 1, &vertices);
                    commands->IASetIndexBuffer(&indices);
                    commands->DrawIndexedInstanced(mesh.IndexCount, (uint)(end - start), 0, 0, (uint)start);
                    draws++;
                    start = end;
                }
            }
            RecordPropStatistics(draws, count);
        }
    }

    private bool TryInitializeProps(D3D12PropCells stream)
    {
        if (_propUnavailable)
            return false;
        if (_propPipeline != null)
            return true;
        try
        {
            InitializeProps(stream);
            return true;
        }
        catch (GpuNativeException) when (!IsRemoved)
        {
            // No prop work can reference these partial allocations before initialization succeeds.
            ReleaseProps();
            _propUnavailable = true;
            return false;
        }
    }

    private void InitializeProps(D3D12PropCells stream)
    {

        // Physical indices are independent of logical cells. Only charged resident and
        // pending records need space, including when every pending cell is a replacement.
        var residentRecords = Math.Min(stream.Limits.ResidentInstances, stream.Limits.Cells * stream.InstancesPerCell);
        var pendingRecords = Math.Min(stream.Limits.PendingInstances, stream.Limits.PendingCells * stream.InstancesPerCell);
        var payloadRecords = residentRecords + pendingRecords;
        _propPayloadCapacity = payloadRecords * PropInstanceData.ByteSize;
        var data = Buffer((ulong)(_propPayloadCapacity + 16 * stream.Limits.ResidentInstances));
        var geometry = Buffer((ulong)stream.GeometryBytes);
        var dataBytes = _device->GetResourceAllocationInfo(1, 1, &data).SizeInBytes;
        var geometryBytes = _device->GetResourceAllocationInfo(1, 1, &geometry).SizeInBytes;
        if (dataBytes > 64 * 1024 * 1024 || geometryBytes > 64 * 1024 * 1024 - dataBytes)
            throw new GpuNativeException("Prop native allocation exceeds 64 MiB", unchecked((int)0x8007000E));

        _propData = CreateResource(_device, _meshUploadHeap, data, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
        ObserveResource(_propData, D3D12ResourceOwner.Props, dataBytes);
        PropInitializationForTests?.Invoke();
        _propGeometry = CreateResource(_device, _meshUploadHeap, geometry, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
        ObserveResource(_propGeometry, D3D12ResourceOwner.Props, geometryBytes);
        _propMapped = MapProp(_propData);
        _propGeometryMapped = MapProp(_propGeometry);
        _propNativeBytes = (long)(dataBytes + geometryBytes);
        _propMeshes = new PackedMesh[stream.Assets.Length * 2];
        _visibleProps = new VisibleProp[stream.Limits.ResidentInstances];
        _propResidentPayloads = new int[stream.Limits.Cells][];
        _propUploads = new PropUpload[stream.Limits.PendingCells];
        _propCandidateBySlot = new int[stream.Limits.Cells];
        _propPreparedUploads = new int[stream.Limits.PendingCells];
        _propFreePayloads = new int[payloadRecords];
        _propFreeCount = payloadRecords;
        for (var i = 0; i < payloadRecords; i++)
            _propFreePayloads[i] = payloadRecords - i - 1;
        _propReferences = new PropReference[stream.Limits.ResidentInstances];
        _propWrittenReferences = new PropReference[stream.Limits.ResidentInstances];
        _propRoot = CreateFrameRoot(FrameConstantCount, props: true);
        _propPipeline = CreateFramePipeline(_propRoot, sky: false, props: true);
    }

    private static byte* MapProp(ID3D12Resource* resource)
    {
        var range = new D3D12_RANGE();
        void* mapped;
        Check(resource->Map(0, &range, &mapped), "Map prop upload");
        return (byte*)mapped;
    }

    private void PreparePropUploads(D3D12PropCells stream)
    {
        _propNextReady = _nextPropRequest;
        Array.Fill(_propCandidateBySlot, -1);
        for (var slot = 0; slot < _propResidentPayloads.Length; slot++)
        {
            if (_propResidentPayloads[slot] is not { } payload || stream.Admission.GetResidentCount(slot) != 0)
                continue;
            RecyclePropPayload(payload);
            _propResidentPayloads[slot] = null;
        }
        for (var index = 0; index < _propUploads.Length; index++)
        {
            var upload = _propUploads[index];
            if (upload.Request.Generation == 0 || upload.Request == stream.Ready[index].Request)
                continue;
            RecyclePropPayload(upload.Payload);
            _propUploads[index] = default;
        }

        var available = D3D12PropCells.MaximumUploadBytes - Math.Max(16 * stream.Limits.ResidentInstances, _propMetadataWritten);
        while (_propMeshCursor < _propMeshes!.Length)
        {
            var asset = stream.Assets[_propMeshCursor / 2];
            var mesh = (_propMeshCursor & 1) == 0 ? asset.Near : asset.Far;
            var bytes = MeshBytes(mesh);
            if (bytes > available - _propPayloadWritten)
                return;
            _propMeshes[_propMeshCursor++] = PackMesh(new(mesh, Vector3.Zero), _propGeometryMapped,
                _propGeometry->GetGPUVirtualAddress(), ref _propGeometryOffset);
            _propPayloadWritten += bytes;
        }

        for (var scan = 0; scan < stream.Ready.Length; scan++)
        {
            var index = (_nextPropRequest + scan) % stream.Ready.Length;
            var ready = stream.Ready[index];
            var request = ready.Request;
            if (request.Generation == 0)
                continue;
            var result = stream.Admission.ReadStagedLength(request, out var slot, out var count);
            if (result != PropCellResult.Accepted)
                continue;

            ref var upload = ref _propUploads[index];
            var writeCount = Math.Min(count - upload.Written, (available - _propPayloadWritten) / PropInstanceData.ByteSize);
            if (upload.Request.Generation == 0 && (writeCount != 0 || count == 0))
            {
                ReservePropPayload(ready.PayloadIndices);
                upload = new(request, slot, ready.PayloadIndices);
            }
            if (writeCount != 0)
                CopyPropPayload(stream, ref upload, writeCount);
            if (upload.Request.Generation == 0 || upload.Written != count)
            {
                _propNextReady = index;
                break;
            }

            stream.Admission.PrepareFrameInstall(request, slot);
            _propCandidateBySlot[slot] = index;
            _propPreparedUploads[_propPreparedCount++] = index;
        }
    }

    private void ReservePropPayload(int[] payload)
    {
        for (var i = 0; i < payload.Length; i++)
            payload[i] = _propFreePayloads[--_propFreeCount];
    }

    private void RecyclePropPayload(int[] payload)
    {
        for (var i = payload.Length - 1; i >= 0; i--)
            _propFreePayloads[_propFreeCount++] = payload[i];
    }

    private void CopyPropPayload(D3D12PropCells stream, ref PropUpload upload, int count)
    {
        var end = upload.Written + count;
        while (upload.Written < end)
        {
            var first = upload.Written;
            var physical = upload.Payload[first];
            var run = 1;
            while (first + run < end && upload.Payload[first + run] == physical + run)
                run++;
            stream.Admission.CopyStagedRange(upload.Slot, first,
                new Span<PropInstanceData>(_propMapped + physical * PropInstanceData.ByteSize, run));
            upload.Written += run;
            _propPayloadWritten += run * PropInstanceData.ByteSize;
        }
    }

    private bool WritePropReferences(int count)
    {
        var changed = 0;
        for (var i = 0; i < count; i++)
            if (i >= _propWrittenReferenceCount || _propReferences[i] != _propWrittenReferences[i])
                changed++;
        var bytes = changed * sizeof(PropReference);
        if (bytes > D3D12PropCells.MaximumUploadBytes - _propPayloadWritten - _propMetadataWritten)
            return false;

        // The last submission retired before recording. An identical retry can reuse
        // mapped references and payloads while keeping its original byte charge.
        var metadata = new Span<PropReference>(_propMapped + _propPayloadCapacity, count);
        for (var i = 0; i < count; i++)
        {
            if (i < _propWrittenReferenceCount && _propReferences[i] == _propWrittenReferences[i])
                continue;
            metadata[i] = _propReferences[i];
            _propWrittenReferences[i] = _propReferences[i];
        }
        _propWrittenReferenceCount = Math.Max(_propWrittenReferenceCount, count);
        _propMetadataWritten += bytes;
        return true;
    }

    private void RecordPropStatistics(int draws, int count) => _propFrameStatistics = PropStatistics(draws, count);

    private D3D12PropStatistics PropStatistics(int draws, int count) =>
        new(_propPayloadWritten + _propMetadataWritten, _propPayloadWritten, _propMetadataWritten,
            draws, count, 0, _propNativeBytes);

    private bool RecordedPropsAreCurrent() => _propFrameStream is not { } stream || stream.ViewVersion == _propFrameVersion;

    private void PublishRecordedProps(D3D12PropCells? deferredProps)
    {
        if (_propFrameStream is { } stream && RecordedPropsAreCurrent())
        {
            if (_propAdmissionPrepared)
            {
                stream.Admission.CommitFrame();
                _propAdmissionPrepared = false;
                for (var i = 0; i < _propPreparedCount; i++)
                {
                    var index = _propPreparedUploads[i];
                    var upload = _propUploads[index];
                    if (_propResidentPayloads[upload.Slot] is { } predecessor)
                        RecyclePropPayload(predecessor);
                    _propResidentPayloads[upload.Slot] = upload.Payload;
                    stream.Ready[index] = default;
                    _propUploads[index] = default;
                }
                _nextPropRequest = _propNextReady;
            }
            if (_propFrameStatistics is { } statistics)
            {
                var deferred = 0;
                if (_propReportPendingWorkers)
                    deferred = stream.Admission.Usage.PendingCells;
                else if (_propReportReady)
                    deferred = CountReadyProps(stream);
                stream.Statistics = statistics with { DeferredCells = deferred };
            }
        }
        if (deferredProps is not null)
        {
            lock (deferredProps.Gate)
                deferredProps.Statistics = PropStatistics(0, 0) with { DeferredCells = CountReadyProps(deferredProps) };
        }
        ResetPropUploadBudget();
    }

    private static int CountReadyProps(D3D12PropCells stream)
    {
        var count = 0;
        foreach (var candidate in stream.Ready)
            if (candidate.Request.Generation != 0)
                count++;
        return count;
    }

    private void AbortRecordedProps()
    {
        if (_propAdmissionPrepared)
            _propFrameStream!.Admission.AbortFrame();
        _propAdmissionPrepared = false;
        _propReportPendingWorkers = false;
        _propReportReady = false;
        _propFrameStream = null;
        _propFrameStatistics = null;
        _propPreparedCount = 0;
    }

    private void ResetPropUploadBudget()
    {
        _propUploadSequence = null;
        _propPayloadWritten = 0;
        _propMetadataWritten = 0;
        _propWrittenReferenceCount = 0;
    }

    private static bool SameGroup(VisibleProp left, VisibleProp right) =>
        left.Model == right.Model && left.Material == right.Material && left.Lod == right.Lod;

    private PackedMesh FindPropMesh(D3D12PropCells stream, VisibleProp prop)
    {
        for (var i = 0; i < stream.Assets.Length; i++)
            if (stream.Assets[i].Model.Model == prop.Model && stream.Assets[i].Model.Material == prop.Material)
                return _propMeshes![2 * i + prop.Lod];
        throw new InvalidOperationException("Unknown resident prop model.");
    }

    private static void ExtractPropFrustum(Matrix4x4 matrix, Span<PropFrustumPlane> planes)
    {
        var x = new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41);
        var y = new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42);
        var z = new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
        var w = new Vector4(matrix.M14, matrix.M24, matrix.M34, matrix.M44);
        planes[0] = Plane(w + x);
        planes[1] = Plane(w - x);
        planes[2] = Plane(w + y);
        planes[3] = Plane(w - y);
        planes[4] = Plane(z);
        planes[5] = Plane(w - z);

        static PropFrustumPlane Plane(Vector4 coefficients)
        {
            var normal = new Vector3(coefficients.X, coefficients.Y, coefficients.Z);
            var length = normal.Length();
            // Very large far planes can round to infinity in the projection.
            return length > 0 ? new(normal / length, coefficients.W / length) : new(Vector3.UnitZ, double.MaxValue);
        }
    }

    private void ReleaseProps()
    {
        if (_propMapped != null)
            _propData->Unmap(0, null);
        if (_propGeometryMapped != null)
            _propGeometry->Unmap(0, null);
        ReleaseObservedResource(_propData);
        ReleaseObservedResource(_propGeometry);
        Release(_propPipeline);
        Release(_propRoot);
        _propMapped = null;
        _propGeometryMapped = null;
        _propData = null;
        _propGeometry = null;
        _propPipeline = null;
        _propRoot = null;
        _propNativeBytes = 0;
        if (PropStream is { } stream)
        {
            lock (stream.Gate)
                stream.Statistics = default;
        }
    }
}
