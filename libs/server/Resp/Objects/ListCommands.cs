﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using Garnet.common;
using Tsavorite.core;

namespace Garnet.server
{
    internal sealed unsafe partial class RespServerSession : ServerSessionBase
    {
        /// <summary>
        /// LPUSH key element[element...]
        /// RPUSH key element [element ...]
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private unsafe bool ListPush<TGarnetApi>(RespCommand command, int count, ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (count < 2)
            {
                return AbortWithWrongNumberOfArguments(command.ToString(), count);
            }

            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            var ptr = sbKey.ToPointer() + sbKey.Length + 2;

            if (NetworkSingleKeySlotVerify(keyBytes, false))
            {
                return true;
            }

            // Prepare input
            var inputPtr = (ObjectInputHeader*)(ptr - sizeof(ObjectInputHeader));

            // Save old values on buffer for possible revert
            var save = *inputPtr;

            var inputCount = count - 1;
            // Prepare length of header in input buffer
            var inputLength = (int)(recvBufferPtr + bytesRead - (byte*)inputPtr);

            ListOperation lop =
                command switch
                {
                    RespCommand.LPUSH => ListOperation.LPUSH,
                    RespCommand.LPUSHX => ListOperation.LPUSHX,
                    RespCommand.RPUSH => ListOperation.RPUSH,
                    RespCommand.RPUSHX => ListOperation.RPUSHX,
                    _ => throw new Exception($"Unexpected {nameof(ListOperation)}: {command}")
                };

            // Prepare header in input buffer
            inputPtr->header.type = GarnetObjectType.List;
            inputPtr->header.flags = 0;
            inputPtr->header.ListOp = lop;
            inputPtr->arg1 = inputCount;

            var input = new ArgSlice((byte*)inputPtr, inputLength);

            ObjectOutputHeader output;

            GarnetStatus status;

            if (command == RespCommand.LPUSH || command == RespCommand.LPUSHX)
                status = storageApi.ListLeftPush(keyBytes, input, out output);
            else
                status = storageApi.ListRightPush(keyBytes, input, out output);

            // Restore input buffer
            *inputPtr = save;

            if (status == GarnetStatus.WRONGTYPE)
            {
                while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                    SendAndReset();
            }
            else
            {
                // Write result to output
                while (!RespWriteUtils.WriteInteger(output.result1, ref dcurr, dend))
                    SendAndReset();
            }
            return true;
        }

        /// <summary>
        /// LPOP key [count]
        /// RPOP key [count]
        /// </summary>
        /// <param name="command"></param>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private unsafe bool ListPop<TGarnetApi>(RespCommand command, int count, ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (count < 1)
            {
                return AbortWithWrongNumberOfArguments(command.ToString(), count);
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            var ptr = sbKey.ToPointer() + sbKey.Length + 2;

            var popCount = 1;

            if (count == 2)
            {
                // Read count
                var popCountSlice = parseState.GetArgSliceByRef(1);
                if (!NumUtils.TryParse(popCountSlice.ReadOnlySpan, out popCount))
                {
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_NOT_INTEGER, ref dcurr, dend))
                        SendAndReset();
                    return true;
                }

                var sbPopCount = popCountSlice.SpanByte;
                ptr = sbPopCount.ToPointer() + sbPopCount.Length + 2;
            }

            if (NetworkSingleKeySlotVerify(keyBytes, false))
            {
                return true;
            }

            // Prepare input
            var inputPtr = (ObjectInputHeader*)(ptr - sizeof(ObjectInputHeader));

            // Save old values on buffer for possible revert
            var save = *inputPtr;

            // Prepare GarnetObjectStore output
            var outputFooter = new GarnetObjectStoreOutput { spanByteAndMemory = new SpanByteAndMemory(dcurr, (int)(dend - dcurr)) };

            // Prepare length of header in input buffer
            var inputLength = (int)(recvBufferPtr + bytesRead - (byte*)inputPtr);

            ListOperation lop =
                command switch
                {
                    RespCommand.LPOP => ListOperation.LPOP,
                    RespCommand.RPOP => ListOperation.RPOP,
                    _ => throw new Exception($"Unexpected {nameof(ListOperation)}: {command}")
                };

            // Prepare header in input buffer
            inputPtr->header.type = GarnetObjectType.List;
            inputPtr->header.flags = 0;
            inputPtr->header.ListOp = lop;
            inputPtr->arg1 = popCount;

            GarnetStatus statusOp;

            if (command == RespCommand.LPOP)
                statusOp = storageApi.ListLeftPop(keyBytes, new ArgSlice((byte*)inputPtr, inputLength), ref outputFooter);
            else
                statusOp = storageApi.ListRightPop(keyBytes, new ArgSlice((byte*)inputPtr, inputLength), ref outputFooter);

            // Reset input buffer
            *inputPtr = save;

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //process output
                    var objOutputHeader = ProcessOutputWithHeader(outputFooter.spanByteAndMemory);
                    break;
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.WriteDirect(CmdStrings.RESP_ERRNOTFOUND, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        private bool ListBlockingPop<TGarnetApi>(RespCommand command, int count)
            where TGarnetApi : IGarnetApi
        {
            if (count < 2)
            {
                return AbortWithWrongNumberOfArguments(command.ToString(), count);
            }

            var keysBytes = new byte[count - 1][];

            for (var i = 0; i < keysBytes.Length; i++)
            {
                keysBytes[i] = parseState.GetArgSliceByRef(i).SpanByte.ToByteArray();
            }

            if (NetworkMultiKeySlotVerify(readOnly: false, firstKey: 0, lastKey: -2))
                return true;

            var timeoutSlice = parseState.GetArgSliceByRef(count - 1);
            if (!NumUtils.TryParse(timeoutSlice.ReadOnlySpan, out double timeout))
            {
                while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_TIMEOUT_NOT_VALID_FLOAT, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            var result = itemBroker.GetCollectionItemAsync(command, keysBytes, this, timeout).Result;

            if (!result.Found)
            {
                while (!RespWriteUtils.WriteNullArray(ref dcurr, dend))
                    SendAndReset();
            }
            else
            {
                while (!RespWriteUtils.WriteArrayLength(2, ref dcurr, dend))
                    SendAndReset();

                while (!RespWriteUtils.WriteBulkString(new Span<byte>(result.Key), ref dcurr, dend))
                    SendAndReset();

                while (!RespWriteUtils.WriteBulkString(new Span<byte>(result.Item), ref dcurr, dend))
                    SendAndReset();
            }

            return true;
        }

        private unsafe bool ListBlockingMove<TGarnetApi>(RespCommand command, int count)
            where TGarnetApi : IGarnetApi
        {
            if (count != 5)
            {
                return AbortWithWrongNumberOfArguments(command.ToString(), count);
            }

            var cmdArgs = new ArgSlice[] { default, default, default };

            var srcKey = parseState.GetArgSliceByRef(0);

            if (NetworkSingleKeySlotVerify(srcKey.ReadOnlySpan, false))
            {
                return true;
            }

            // Read destination key
            cmdArgs[0] = parseState.GetArgSliceByRef(1);

            if (NetworkSingleKeySlotVerify(cmdArgs[0].ReadOnlySpan, false))
            {
                return true;
            }

            var srcDir = parseState.GetArgSliceByRef(2);
            var dstDir = parseState.GetArgSliceByRef(3);

            var sourceDirection = GetOperationDirection(srcDir);
            var destinationDirection = GetOperationDirection(dstDir);

            if (sourceDirection == OperationDirection.Unknown || destinationDirection == OperationDirection.Unknown)
            {
                return AbortWithErrorMessage(count, CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
            }

            var pSrcDir = (byte*)&sourceDirection;
            var pDstDir = (byte*)&destinationDirection;
            cmdArgs[1] = new ArgSlice(pSrcDir, 1);
            cmdArgs[2] = new ArgSlice(pDstDir, 1);

            var timeoutSlice = parseState.GetArgSliceByRef(4);
            if (!NumUtils.TryParse(timeoutSlice.ReadOnlySpan, out double timeout))
            {
                while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_TIMEOUT_NOT_VALID_FLOAT, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            var result = itemBroker.MoveCollectionItemAsync(command, srcKey.ToArray(), this, timeout,
                cmdArgs).Result;

            if (!result.Found)
            {
                while (!RespWriteUtils.WriteNull(ref dcurr, dend))
                    SendAndReset();
            }
            else
            {
                while (!RespWriteUtils.WriteBulkString(new Span<byte>(result.Item), ref dcurr, dend))
                    SendAndReset();
            }

            return true;
        }

        /// <summary>
        /// LLEN key
        /// Gets the length of the list stored at key.
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListLength<TGarnetApi>(int count, ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (count != 1)
            {
                return AbortWithWrongNumberOfArguments("LLEN", count);
            }

            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            var ptr = sbKey.ToPointer() + sbKey.Length + 2;

            if (NetworkSingleKeySlotVerify(keyBytes, true))
            {
                return true;
            }

            // Prepare input
            var inputPtr = (ObjectInputHeader*)(ptr - sizeof(ObjectInputHeader));

            // save old values
            var save = *inputPtr;

            // Prepare length of header in input buffer
            var inputLength = (int)(recvBufferPtr + bytesRead - ptr) + sizeof(ObjectInputHeader);

            // Prepare header in input buffer
            inputPtr->header.type = GarnetObjectType.List;
            inputPtr->header.flags = 0;
            inputPtr->header.ListOp = ListOperation.LLEN;
            inputPtr->arg1 = count;

            var status = storageApi.ListLength(keyBytes, new ArgSlice((byte*)inputPtr, inputLength), out var output);

            // Restore input buffer
            *inputPtr = save;

            switch (status)
            {
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.WriteDirect(CmdStrings.RESP_RETURN_VAL_0, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
                default:
                    // Process output
                    while (!RespWriteUtils.WriteInteger(output.result1, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// LTRIM key start stop
        /// Trim an existing list so it only contains the specified range of elements.
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListTrim<TGarnetApi>(int count, ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (count != 3)
            {
                return AbortWithWrongNumberOfArguments("LTRIM", count);
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Read the parameters(start and stop) from LTRIM
            var startSlice = parseState.GetArgSliceByRef(1);
            var stopSlice = parseState.GetArgSliceByRef(2);

            if (!NumUtils.TryParse(startSlice.ReadOnlySpan, out int start) ||
                !NumUtils.TryParse(stopSlice.ReadOnlySpan, out int stop))
            {
                while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_NOT_INTEGER, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            var sbStop = stopSlice.SpanByte;
            var ptr = sbStop.ToPointer() + sbStop.Length + 2;

            if (NetworkSingleKeySlotVerify(keyBytes, false))
            {
                return true;
            }

            // Prepare input
            var inputPtr = (ObjectInputHeader*)(ptr - sizeof(ObjectInputHeader));

            // Save old values on buffer for possible revert
            var save = *inputPtr;

            // Prepare length of header in input buffer
            var inputLength = (int)(recvBufferPtr + bytesRead - (byte*)inputPtr);

            // Prepare header in input buffer
            inputPtr->header.type = GarnetObjectType.List;
            inputPtr->header.flags = 0;
            inputPtr->header.ListOp = ListOperation.LTRIM;
            inputPtr->arg1 = start;
            inputPtr->arg2 = stop;

            var status = storageApi.ListTrim(keyBytes, new ArgSlice((byte*)inputPtr, inputLength));

            // Restore input buffer
            *inputPtr = save;

            switch (status)
            {
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
                default:
                    //GarnetStatus.OK or NOTFOUND have same result
                    // no need to process output, just send OK
                    while (!RespWriteUtils.WriteDirect(CmdStrings.RESP_OK, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// Gets the specified elements of the list stored at key.
        /// LRANGE key start stop
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListRange<TGarnetApi>(int count, ref TGarnetApi storageApi)
             where TGarnetApi : IGarnetApi
        {
            if (count != 3)
            {
                return AbortWithWrongNumberOfArguments("LRANGE", count);
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Read count start and end params for LRANGE
            var startSlice = parseState.GetArgSliceByRef(1);
            var endSlice = parseState.GetArgSliceByRef(2);

            if (!NumUtils.TryParse(startSlice.ReadOnlySpan, out int start) ||
                !NumUtils.TryParse(endSlice.ReadOnlySpan, out int end))
            {
                while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_NOT_INTEGER, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            var sbEnd = endSlice.SpanByte;
            var ptr = sbEnd.ToPointer() + sbEnd.Length + 2;

            if (NetworkSingleKeySlotVerify(keyBytes, true))
            {
                return true;
            }

            // Prepare input
            var inputPtr = (ObjectInputHeader*)(ptr - sizeof(ObjectInputHeader));

            // Save old values on buffer for possible revert
            var save = *inputPtr;

            // Prepare GarnetObjectStore output
            var outputFooter = new GarnetObjectStoreOutput { spanByteAndMemory = new SpanByteAndMemory(dcurr, (int)(dend - dcurr)) };

            // Prepare length of header in input buffer
            var inputLength = (int)(recvBufferPtr + bytesRead - (byte*)inputPtr);

            inputPtr->header.type = GarnetObjectType.List;
            inputPtr->header.flags = 0;
            inputPtr->header.ListOp = ListOperation.LRANGE;
            inputPtr->arg1 = start;
            inputPtr->arg2 = end;

            var statusOp = storageApi.ListRange(keyBytes, new ArgSlice((byte*)inputPtr, inputLength), ref outputFooter);

            // Reset input buffer
            *inputPtr = save;

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //process output
                    ProcessOutputWithHeader(outputFooter.spanByteAndMemory);
                    break;
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.WriteDirect(CmdStrings.RESP_EMPTYLIST, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }
            return true;
        }

        /// <summary>
        /// Returns the element at index.
        /// LINDEX key index
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListIndex<TGarnetApi>(int count, ref TGarnetApi storageApi)
             where TGarnetApi : IGarnetApi
        {
            if (count != 2)
            {
                return AbortWithWrongNumberOfArguments("LINDEX", count);
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Read index param
            var indexSlice = parseState.GetArgSliceByRef(1);
            if (!NumUtils.TryParse(indexSlice.ReadOnlySpan, out int index))
            {
                while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_NOT_INTEGER, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            var sbIndex = indexSlice.SpanByte;
            var ptr = sbIndex.ToPointer() + sbIndex.Length + 2;

            if (NetworkSingleKeySlotVerify(keyBytes, true))
            {
                return true;
            }

            // Prepare input
            var inputPtr = (ObjectInputHeader*)(ptr - sizeof(ObjectInputHeader));

            // Save input buffer
            var save = *inputPtr;

            // Prepare GarnetObjectStore output
            var outputFooter = new GarnetObjectStoreOutput { spanByteAndMemory = new SpanByteAndMemory(dcurr, (int)(dend - dcurr)) };

            var inputLength = (int)(recvBufferPtr + bytesRead - (byte*)inputPtr);

            // Prepare header in input buffer
            inputPtr->header.type = GarnetObjectType.List;
            inputPtr->header.flags = 0;
            inputPtr->header.ListOp = ListOperation.LINDEX;
            inputPtr->arg1 = index;

            var statusOp = storageApi.ListIndex(keyBytes, new ArgSlice((byte*)inputPtr, inputLength), ref outputFooter);

            //restore input
            *inputPtr = save;

            ReadOnlySpan<byte> error = default;

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //process output
                    var objOutputHeader = ProcessOutputWithHeader(outputFooter.spanByteAndMemory);
                    if (objOutputHeader.result1 == -1)
                        error = CmdStrings.RESP_ERRNOTFOUND;
                    break;
                case GarnetStatus.NOTFOUND:
                    error = CmdStrings.RESP_ERRNOTFOUND;
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            if (error != default)
            {
                while (!RespWriteUtils.WriteDirect(error, ref dcurr, dend))
                    SendAndReset();
            }

            return true;
        }

        /// <summary>
        /// Inserts a new element in the list stored at key either before or after a value pivot
        /// LINSERT key BEFORE|AFTER pivot element
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListInsert<TGarnetApi>(int count, ref TGarnetApi storageApi)
             where TGarnetApi : IGarnetApi
        {
            if (count != 4)
            {
                return AbortWithWrongNumberOfArguments("LINSERT", count);
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            var ptr = sbKey.ToPointer() + sbKey.Length + 2;

            if (NetworkSingleKeySlotVerify(keyBytes, false))
            {
                return true;
            }

            // Prepare input
            var inputPtr = (ObjectInputHeader*)(ptr - sizeof(ObjectInputHeader));

            // Save old values
            var save = *inputPtr;

            // Prepare length of header in input buffer
            var inputLength = (int)(recvBufferPtr + bytesRead - (byte*)inputPtr);

            // Prepare header in input buffer
            inputPtr->header.type = GarnetObjectType.List;
            inputPtr->header.flags = 0;
            inputPtr->header.ListOp = ListOperation.LINSERT;
            inputPtr->arg1 = 0;

            var statusOp = storageApi.ListInsert(keyBytes, new ArgSlice((byte*)inputPtr, inputLength), out var output);

            // Restore input buffer
            *inputPtr = save;

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //check for partial execution
                    if (output.result1 == int.MinValue)
                        return false;
                    //process output
                    while (!RespWriteUtils.WriteInteger(output.result1, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.WriteDirect(CmdStrings.RESP_RETURN_VAL_0, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// LREM key count element
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListRemove<TGarnetApi>(int count, ref TGarnetApi storageApi)
              where TGarnetApi : IGarnetApi
        {
            // if params are missing return error
            if (count != 3)
            {
                return AbortWithWrongNumberOfArguments("LREM", count);
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Get count parameter
            var countSlice = parseState.GetArgSliceByRef(1);
            if (!NumUtils.TryParse(countSlice.ReadOnlySpan, out int nCount))
            {
                while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_NOT_INTEGER, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            var sbCount = countSlice.SpanByte;
            var ptr = sbCount.ToPointer() + sbCount.Length + 2;

            if (NetworkSingleKeySlotVerify(keyBytes, false))
            {
                return true;
            }

            // Prepare input
            var inputPtr = (ObjectInputHeader*)(ptr - sizeof(ObjectInputHeader));

            // Save old values
            var save = *inputPtr;

            // Prepare length of header in input buffer
            var inputLength = (int)(recvBufferPtr + bytesRead - (byte*)inputPtr);

            // Prepare header in input buffer
            inputPtr->header.type = GarnetObjectType.List;
            inputPtr->header.flags = 0;
            inputPtr->header.ListOp = ListOperation.LREM;
            inputPtr->arg1 = nCount;

            var statusOp = storageApi.ListRemove(keyBytes, new ArgSlice((byte*)inputPtr, inputLength), out var output);
            // Restore input buffer
            *inputPtr = save;

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //check for partial execution
                    if (output.result1 == int.MinValue)
                        return false;
                    //process output
                    while (!RespWriteUtils.WriteInteger(output.result1, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.WriteDirect(CmdStrings.RESP_RETURN_VAL_0, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }


        /// <summary>
        /// LMOVE source destination [LEFT | RIGHT] [LEFT | RIGHT]
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListMove<TGarnetApi>(int count, ref TGarnetApi storageApi)
             where TGarnetApi : IGarnetApi
        {
            if (count != 4)
            {
                return AbortWithWrongNumberOfArguments("LMOVE", count);
            }

            var srcKey = parseState.GetArgSliceByRef(0);
            var dstKey = parseState.GetArgSliceByRef(1);

            if (NetworkSingleKeySlotVerify(srcKey.ReadOnlySpan, false) ||
                NetworkSingleKeySlotVerify(dstKey.ReadOnlySpan, false))
            {
                return true;
            }

            var srcDirSlice = parseState.GetArgSliceByRef(2);
            var dstDirSlice = parseState.GetArgSliceByRef(3);

            var sourceDirection = GetOperationDirection(srcDirSlice);
            var destinationDirection = GetOperationDirection(dstDirSlice);

            if (sourceDirection == OperationDirection.Unknown || destinationDirection == OperationDirection.Unknown)
            {
                return AbortWithErrorMessage(count, CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
            }

            if (!ListMove(srcKey, dstKey, sourceDirection, destinationDirection, out var node,
                    ref storageApi, out var garnetStatus))
                return false;

            switch (garnetStatus)
            {
                case GarnetStatus.OK:
                    if (node != null)
                    {
                        while (!RespWriteUtils.WriteBulkString(node, ref dcurr, dend))
                            SendAndReset();
                    }
                    else
                    {
                        while (!RespWriteUtils.WriteNull(ref dcurr, dend))
                            SendAndReset();
                    }

                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// RPOPLPUSH source destination
        /// </summary>
        /// <param name="count"></param>
        /// <param name="ptr"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListRightPopLeftPush<TGarnetApi>(int count, byte* ptr, ref TGarnetApi storageApi)
            where TGarnetApi : IGarnetApi
        {
            if (count != 2)
            {
                return AbortWithWrongNumberOfArguments("RPOPLPUSH", count);
            }

            var srcKey = parseState.GetArgSliceByRef(0);
            var dstKey = parseState.GetArgSliceByRef(1);

            if (NetworkSingleKeySlotVerify(srcKey.ReadOnlySpan, false) ||
                NetworkSingleKeySlotVerify(dstKey.ReadOnlySpan, false))
            {
                return true;
            }

            if (!ListMove(srcKey, dstKey, OperationDirection.Right, OperationDirection.Left,
                    out var node, ref storageApi, out var garnetStatus))
                return false;

            switch (garnetStatus)
            {
                case GarnetStatus.OK:
                    if (node != null)
                    {
                        while (!RespWriteUtils.WriteBulkString(node, ref dcurr, dend))
                            SendAndReset();
                    }
                    else
                    {
                        while (!RespWriteUtils.WriteNull(ref dcurr, dend))
                            SendAndReset();
                    }

                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// LMOVE source destination LEFT|RIGHT LEFT|RIGHT
        /// RPOPLPUSH source destination
        /// </summary>
        /// <param name="sourceKey"></param>
        /// <param name="destinationKey"></param>
        /// <param name="sourceDirection"></param>
        /// <param name="destinationDirection"></param>
        /// <param name="node"></param>
        /// <param name="storageApi"></param>
        /// <param name="garnetStatus"></param>
        /// <returns></returns>
        private bool ListMove<TGarnetApi>(ArgSlice sourceKey, ArgSlice destinationKey,
            OperationDirection sourceDirection, OperationDirection destinationDirection, out byte[] node,
            ref TGarnetApi storageApi, out GarnetStatus garnetStatus)
            where TGarnetApi : IGarnetApi
        {
            garnetStatus = GarnetStatus.OK;
            node = null;

            if (NetworkMultiKeySlotVerify(readOnly: true, firstKey: 0, lastKey: 2))
                return true;

            garnetStatus =
                storageApi.ListMove(sourceKey, destinationKey, sourceDirection, destinationDirection, out node);
            return true;
        }

        /// <summary>
        /// Sets the list element at index to element
        /// LSET key index element
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="count"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        public bool ListSet<TGarnetApi>(int count, ref TGarnetApi storageApi)
            where TGarnetApi : IGarnetApi
        {
            if (count != 3)
            {
                return AbortWithWrongNumberOfArguments("LSET", count);
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            var ptr = sbKey.ToPointer() + sbKey.Length + 2;

            if (NetworkSingleKeySlotVerify(keyBytes, true))
            {
                return true;
            }

            // Prepare input
            var inputPtr = (ObjectInputHeader*)(ptr - sizeof(ObjectInputHeader));

            // Save input buffer
            var save = *inputPtr;

            var inputLength = (int)(recvBufferPtr + bytesRead - (byte*)inputPtr);

            // Prepare header in input buffer
            inputPtr->header.type = GarnetObjectType.List;
            inputPtr->header.flags = 0;
            inputPtr->header.ListOp = ListOperation.LSET;
            inputPtr->arg1 = 0;

            // Prepare GarnetObjectStore output
            var outputFooter = new GarnetObjectStoreOutput { spanByteAndMemory = new SpanByteAndMemory(dcurr, (int)(dend - dcurr)) };

            var statusOp = storageApi.ListSet(keyBytes, new ArgSlice((byte*)inputPtr, inputLength), ref outputFooter);

            //restore input
            *inputPtr = save;

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //process output
                    ProcessOutputWithHeader(outputFooter.spanByteAndMemory);
                    break;
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_GENERIC_NOSUCHKEY, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.WriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }
    }
}