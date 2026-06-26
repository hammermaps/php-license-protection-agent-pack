/*
 * Minimal LZ4 block decompressor — implementation.
 *
 * Implements the LZ4 raw-block format as specified at:
 *   https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
 *
 * Compatible with K4os.Compression.LZ4's LZ4Codec.Encode output and with
 * liblz4's LZ4_compress_default / LZ4_compress_HC output.
 *
 * Only decompression is implemented (we never need to compress in the loader).
 *
 * License: BSD 2-Clause (same as the reference LZ4 implementation).
 *
 * LZ4 block format summary:
 *   A block is a sequence of <sequences>. Each sequence:
 *     1. 1 token byte:
 *          bits [7:4] = literal length (0–14; if 15 → read more below)
 *          bits [3:0] = match length excess (0–14; if 15 → read more below;
 *                       actual match length = excess + 4)
 *     2. Optional extra literal-length bytes: add 255 each until < 255.
 *     3. Literal bytes (count from steps 1–2).
 *     4. 2-byte little-endian match offset (distance back in output).
 *        — ABSENT for the last sequence in the block.
 *     5. Optional extra match-length bytes (same as step 2).
 *     6. Copy (match length) bytes from (output − offset).
 *        — ABSENT for the last sequence.
 *
 *   The last sequence only has a token + optional extra lengths + literals.
 *   The decoder detects this when the compressed-input pointer reaches the
 *   end of the compressed buffer after step 3.
 */

#include "lz4_decompress.h"
#include <stdint.h>
#include <string.h>

int LZ4_decompress_safe(const char *src, char *dst,
                        int compressedSize, int dstCapacity)
{
    if (!src || !dst || compressedSize < 0 || dstCapacity < 0)
        return -1;

    const uint8_t *ip     = (const uint8_t *)src;
    const uint8_t *iend   = ip + (unsigned)compressedSize;
    uint8_t       *op     = (uint8_t *)dst;
    uint8_t       *oend   = op + (unsigned)dstCapacity;

    for (;;) {
        if (ip >= iend) break;      /* end of compressed stream */

        /* ── 1. Read token ── */
        uint8_t token = *ip++;
        int llen = (token >> 4) & 0xF;
        int mex  = (token)      & 0xF;

        /* ── 2. Extra literal-length bytes ── */
        if (llen == 15) {
            int extra;
            do {
                if (ip >= iend) return -1;
                extra = *ip++;
                llen += extra;
                if (llen < 0) return -1;  /* overflow guard */
            } while (extra == 255);
        }

        /* ── 3. Copy literals ── */
        if (llen > 0) {
            if (ip + llen > iend)   return -1;  /* compressed truncated */
            if (op + llen > oend)   return -1;  /* output overflow */
            memcpy(op, ip, (size_t)llen);
            ip += llen;
            op += llen;
        }

        /* End of block: last sequence has no match part */
        if (ip >= iend) break;

        /* ── 4. Read 2-byte little-endian match offset ── */
        if (ip + 2 > iend) return -1;
        uint16_t raw_offset;
        memcpy(&raw_offset, ip, 2);
        ip += 2;
        /* LZ4 stores offset in little-endian; on LE hosts memcpy gives the
           right value; on BE we must byte-swap. */
#if defined(__BYTE_ORDER__) && (__BYTE_ORDER__ == __ORDER_BIG_ENDIAN__)
        raw_offset = (uint16_t)((raw_offset >> 8) | (raw_offset << 8));
#endif
        int offset = (int)raw_offset;
        if (offset == 0) return -1;         /* offset 0 is invalid */

        /* ── 5. Extra match-length bytes ── */
        int mlen = mex + 4;
        if (mex == 15) {
            int extra;
            do {
                if (ip >= iend) return -1;
                extra = *ip++;
                mlen += extra;
                if (mlen < 0) return -1;
            } while (extra == 255);
        }

        /* ── 6. Copy match ── */
        uint8_t *match = op - offset;
        if (match < (uint8_t *)dst) return -1;  /* reference before start */
        if (op + mlen > oend)       return -1;  /* output overflow */

        /*
         * Handle overlapping copies (offset < mlen): must copy
         * byte-by-byte so that each byte sees the previously written value.
         * For non-overlapping: memcpy is fine.
         */
        if (offset >= mlen) {
            memcpy(op, match, (size_t)mlen);
        } else {
            uint8_t *out = op;
            const uint8_t *in = match;
            int n = mlen;
            while (n-- > 0) *out++ = *in++;
        }
        op += mlen;
    }

    return (int)(op - (uint8_t *)dst);
}
