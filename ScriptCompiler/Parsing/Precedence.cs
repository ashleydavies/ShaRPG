﻿namespace ScriptCompiler.Parsing {
    public enum Precedence {
        NONE,
        ASSIGNMENT,
        OR,
        AND,
        EQUALITY,
        COMPARISON,
        TERM,
        FACTOR,
        UNARY,
        CALL,
        MAX
    }
}