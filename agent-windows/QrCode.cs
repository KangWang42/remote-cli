using System;
using System.Text;

namespace RemoteCli {
/// QR code for a short text (byte mode, error correction level M). Versions 1 to 10, up to 213 bytes:
/// enough for the address and password the phone scans.
public static class QrCode {
    static readonly int[] EccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    static readonly int[] Blocks = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };

    /// Dark modules as [row, column] without the quiet zone; null when the text is too long.
    public static bool[,] Encode(string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        for (int version = 1; version <= 10; version++) {
            int countBits = version <= 9 ? 8 : 16;
            if (4 + countBits + bytes.Length * 8 <= DataCodewords(version) * 8) return Build(version, bytes);
        }
        return null;
    }
    static int RawModules(int version) {
        int result = (16 * version + 128) * version + 64;
        if (version >= 2) {
            int align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }
    static int DataCodewords(int version) { return RawModules(version) / 8 - EccPerBlock[version] * Blocks[version]; }

    static bool[,] Build(int version, byte[] bytes) {
        // data bits: mode, length, bytes, terminator, padding
        int capacity = DataCodewords(version), at = 0;
        byte[] data = new byte[capacity];
        Append(data, ref at, 0x4, 4);
        Append(data, ref at, bytes.Length, version <= 9 ? 8 : 16);
        foreach (byte b in bytes) Append(data, ref at, b, 8);
        Append(data, ref at, 0, Math.Min(4, capacity * 8 - at));
        Append(data, ref at, 0, (8 - at % 8) % 8);
        for (int pad = 0xEC; at < capacity * 8; pad ^= 0xEC ^ 0x11) Append(data, ref at, pad, 8);

        // error correction blocks, interleaved
        int blocks = Blocks[version], eccLength = EccPerBlock[version], raw = RawModules(version) / 8;
        int shortBlocks = blocks - raw % blocks, shortLength = raw / blocks;
        byte[][] parts = new byte[blocks][];
        byte[] divisor = RsDivisor(eccLength);
        for (int i = 0, k = 0; i < blocks; i++) {
            int length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            byte[] block = new byte[shortLength + 1];
            Array.Copy(data, k, block, 0, length);
            k += length;
            Array.Copy(RsRemainder(block, length, divisor), 0, block, block.Length - eccLength, eccLength);
            parts[i] = block;
        }
        byte[] all = new byte[raw];
        for (int i = 0, k = 0; i < shortLength + 1; i++)
            for (int j = 0; j < blocks; j++)
                if (i != shortLength - eccLength || j >= shortBlocks) all[k++] = parts[j][i];   // short blocks have no byte in that column

        // function patterns
        int size = version * 4 + 17;
        bool[,] dark = new bool[size, size], locked = new bool[size, size];
        for (int i = 0; i < size; i++) { Set(dark, locked, 6, i, i % 2 == 0); Set(dark, locked, i, 6, i % 2 == 0); }
        Finder(dark, locked, 3, 3, size); Finder(dark, locked, size - 4, 3, size); Finder(dark, locked, 3, size - 4, size);
        int[] positions = Alignment(version, size);
        for (int i = 0; i < positions.Length; i++)
            for (int j = 0; j < positions.Length; j++) {
                if ((i == 0 && j == 0) || (i == 0 && j == positions.Length - 1) || (i == positions.Length - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
                    Set(dark, locked, positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        Format(dark, locked, 0, size);      // reserves the format area; the real mask is written below
        if (version >= 7) {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = version << 12 | rem;
            for (int i = 0; i < 18; i++) {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(dark, locked, a, b, bit); Set(dark, locked, b, a, bit);
            }
        }

        // data modules in the zigzag order
        int index = 0;
        for (int right = size - 1; right >= 1; right -= 2) {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++) {
                    int x = right - j, y = ((right + 1) & 2) == 0 ? size - 1 - vert : vert;
                    if (!locked[y, x] && index < all.Length * 8) { dark[y, x] = ((all[index >> 3] >> (7 - (index & 7))) & 1) != 0; index++; }
                }
        }

        // the mask with the lowest penalty
        int best = 0;
        long lowest = long.MaxValue;
        for (int mask = 0; mask < 8; mask++) {
            Mask(dark, locked, mask, size);
            Format(dark, locked, mask, size);
            long penalty = Penalty(dark, size);
            if (penalty < lowest) { lowest = penalty; best = mask; }
            Mask(dark, locked, mask, size);     // applying the same mask again removes it
        }
        Mask(dark, locked, best, size);
        Format(dark, locked, best, size);
        return dark;
    }
    static void Append(byte[] data, ref int at, int value, int length) {
        for (int i = length - 1; i >= 0; i--, at++) data[at >> 3] |= (byte)(((value >> i) & 1) << (7 - (at & 7)));
    }
    static void Set(bool[,] dark, bool[,] locked, int x, int y, bool value) { dark[y, x] = value; locked[y, x] = true; }
    static void Finder(bool[,] dark, bool[,] locked, int cx, int cy, int size) {
        for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++) {
            int x = cx + dx, y = cy + dy, distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (x >= 0 && x < size && y >= 0 && y < size) Set(dark, locked, x, y, distance != 2 && distance != 4);
        }
    }
    static int[] Alignment(int version, int size) {
        if (version == 1) return new int[0];
        int count = version / 7 + 2, step = (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
        int[] result = new int[count];
        result[0] = 6;
        for (int i = count - 1, position = size - 7; i >= 1; i--, position -= step) result[i] = position;
        return result;
    }
    static bool Bit(int value, int index) { return ((value >> index) & 1) != 0; }
    static void Format(bool[,] dark, bool[,] locked, int mask, int size) {
        int rem = mask;      // level M has format bits 00
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (mask << 10 | rem) ^ 0x5412;
        for (int i = 0; i <= 5; i++) Set(dark, locked, 8, i, Bit(bits, i));
        Set(dark, locked, 8, 7, Bit(bits, 6));
        Set(dark, locked, 8, 8, Bit(bits, 7));
        Set(dark, locked, 7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++) Set(dark, locked, 14 - i, 8, Bit(bits, i));
        for (int i = 0; i < 8; i++) Set(dark, locked, size - 1 - i, 8, Bit(bits, i));
        for (int i = 8; i < 15; i++) Set(dark, locked, 8, size - 15 + i, Bit(bits, i));
        Set(dark, locked, 8, size - 8, true);
    }
    static void Mask(bool[,] dark, bool[,] locked, int mask, int size) {
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) {
            if (locked[y, x]) continue;
            bool invert;
            switch (mask) {
                case 0: invert = (x + y) % 2 == 0; break;
                case 1: invert = y % 2 == 0; break;
                case 2: invert = x % 3 == 0; break;
                case 3: invert = (x + y) % 3 == 0; break;
                case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
            }
            dark[y, x] ^= invert;
        }
    }
    /// Runs of one colour, finder-like sequences, 2x2 blocks and the balance of dark and light: the lower the easier to scan.
    static long Penalty(bool[,] dark, int size) {
        int total = 0;
        long result = 0;
        for (int line = 0; line < size; line++)
            for (int direction = 0; direction < 2; direction++) {
                int run = 1;
                for (int i = 1; i < size; i++) {
                    bool same = direction == 0 ? dark[line, i] == dark[line, i - 1] : dark[i, line] == dark[i - 1, line];
                    if (same) { run++; if (run == 5) result += 3; else if (run > 5) result++; } else run = 1;
                }
            }
        bool[] a = { true, false, true, true, true, false, true, false, false, false, false };
        for (int line = 0; line < size; line++)
            for (int start = 0; start + 11 <= size; start++) {
                bool rowA = true, rowB = true, colA = true, colB = true;
                for (int k = 0; k < 11; k++) {
                    bool r = dark[line, start + k], c = dark[start + k, line];
                    if (r != a[k]) rowA = false;
                    if (r != a[10 - k]) rowB = false;
                    if (c != a[k]) colA = false;
                    if (c != a[10 - k]) colB = false;
                }
                if (rowA) result += 40;
                if (rowB) result += 40;
                if (colA) result += 40;
                if (colB) result += 40;
            }
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) {
            if (dark[y, x]) total++;
            if (y + 1 < size && x + 1 < size && dark[y, x] == dark[y, x + 1] && dark[y, x] == dark[y + 1, x] && dark[y, x] == dark[y + 1, x + 1]) result += 3;
        }
        result += 10L * (Math.Abs(total * 20 - size * size * 10) / (size * size));
        return result;
    }
    static byte[] RsDivisor(int degree) {
        byte[] result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++) {
            for (int j = 0; j < degree; j++) {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }
    static byte[] RsRemainder(byte[] data, int length, byte[] divisor) {
        byte[] result = new byte[divisor.Length];
        for (int k = 0; k < length; k++) {
            int factor = data[k] ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[result.Length - 1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }
    static int Multiply(int x, int y) {
        int z = 0;
        for (int i = 7; i >= 0; i--) {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }
}
}
