namespace STS2_MCP;

public static partial class McpMod
{
    // Shared CIL instruction sizing.
    //
    // Two scanners read method bodies to recover what the shipped game code
    // does: the move-effect scan (McpMod.MoveEffects.cs) and the intent
    // predicate scan (McpMod.Predicates.cs). Both must step instructions by
    // exactly the right width or they desynchronize and read operand bytes as
    // opcodes, so the width table lives here once.

    /// <summary>
    /// Total byte length of the instruction starting at <paramref name="offset"/>.
    /// </summary>
    /// <remarks>
    /// Returns at least 1 for any byte, so a malformed or unknown opcode makes
    /// a scan lose accuracy rather than loop forever.
    /// </remarks>
    internal static int InstructionLength(byte[] il, int offset)
    {
        byte op = il[offset];
        if (op == 0xFE)
        {
            if (offset + 1 >= il.Length)
                return 1;
            return il[offset + 1] switch
            {
                // ldftn / ldvirtftn / initobj / constrained. / sizeof: metadata token
                0x06 or 0x07 or 0x15 or 0x16 or 0x1C => 6,
                // ldarg / ldarga / starg / ldloc / ldloca / stloc: uint16 index
                0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E => 4,
                // unaligned.: uint8 alignment
                0x12 => 3,
                _ => 2,
            };
        }
        // switch: uint32 count followed by that many int32 targets
        if (op == 0x45)
        {
            if (offset + 5 > il.Length)
                return 1;
            int count = System.BitConverter.ToInt32(il, offset + 1);
            if (count < 0)
                return 1;
            return 5 + (4 * count);
        }
        return op switch
        {
            // short-form local/argument index, short branches, leave.s
            0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 => 2,
            0x1F => 2,                    // ldc.i4.s
            >= 0x2B and <= 0x37 => 2,     // br.s .. blt.un.s
            0xDE => 2,                    // leave.s
            // int64 / float64 inline constants
            0x21 or 0x23 => 9,
            // int32 / float32 inline constants
            0x20 or 0x22 => 5,
            // long branches and leave
            (>= 0x38 and <= 0x44) or 0xDD => 5,
            // metadata-token operands
            0x27 or 0x28 or 0x29 or 0x6F => 5,                        // jmp/call/calli/callvirt
            0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 => 5,        // cpobj/ldobj/ldstr/newobj/castclass/isinst
            0x79 => 5,                                                // unbox
            0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x81 => 5, // ld/st fld/sfld, stobj
            0x8C or 0x8D or 0x8F => 5,                                // box/newarr/ldelema
            0xA3 or 0xA4 or 0xA5 => 5,                                // ldelem/stelem/unbox.any
            0xC2 or 0xC6 => 5,                                        // refanyval/mkrefany
            0xD0 => 5,                                                // ldtoken
            _ => 1,
        };
    }
}
