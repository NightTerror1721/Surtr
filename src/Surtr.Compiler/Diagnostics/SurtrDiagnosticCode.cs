#nullable enable

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Identifies what a diagnostic is about, independently of how it is worded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A code is a <em>stable</em> name for a problem. Message text is written for a person and
    /// gets reworded; a test asserting on it breaks for no reason, a suppression keyed on it stops
    /// working, and a translation has nothing to key off. So everything that reports goes through
    /// one of these, and the message carries the specifics.
    /// </para>
    /// <para>
    /// The numbers are grouped by stage and are <b>append-only within a group</b>: a code that has
    /// been published is a name someone may have written down. Lexical problems are 1xxx and
    /// syntactic ones 2xxx; 3xxx is reserved for binding and 4xxx for code generation, so those
    /// stages can be added without renumbering these.
    /// </para>
    /// <para>
    /// The set is deliberately no larger than what the front end actually produces. Inventing a
    /// vocabulary ahead of the passes that would use it would mean guessing at distinctions that
    /// only a real binder can tell are worth making.
    /// </para>
    /// </remarks>
    public enum SurtrDiagnosticCode
    {
        /// <summary>No code. Never reported.</summary>
        None = 0,

        #region Lexical — 1xxx

        /// <summary>A character that begins no token in the language.</summary>
        UnexpectedCharacter = 1001,

        /// <summary>A <c>/*</c> that the file ends before closing.</summary>
        UnterminatedComment = 1002,

        /// <summary>A string literal the line or the file ends before closing.</summary>
        UnterminatedStringLiteral = 1003,

        /// <summary>A character literal holding no character, or more than one.</summary>
        InvalidCharacterLiteral = 1004,

        /// <summary>A literal broken across lines, which neither string nor character literals allow.</summary>
        LiteralSpansLines = 1005,

        /// <summary>An escape sequence that is unrecognised, incomplete, or missing its digits.</summary>
        InvalidEscapeSequence = 1006,

        /// <summary>A numeric literal that does not parse: no digits, or a malformed float.</summary>
        InvalidNumericLiteral = 1007,

        /// <summary>A numeric literal too large for the type it would have.</summary>
        NumericLiteralOutOfRange = 1008,

        #endregion

        #region Syntactic — 2xxx

        /// <summary>A token where the grammar required a different one.</summary>
        UnexpectedToken = 2001,

        /// <summary>A token that begins no declaration, where one was required.</summary>
        ExpectedDeclaration = 2002,

        /// <summary>A token that begins no expression, where one was required.</summary>
        ExpectedExpression = 2003,

        /// <summary>A token that begins no type, where one was required.</summary>
        ExpectedType = 2004,

        /// <summary>A modifier repeated, contradicted, or written where it carries no information.</summary>
        InvalidModifier = 2005,

        /// <summary>An operator overload for a token that cannot be overloaded, or written in the wrong shape.</summary>
        InvalidOperatorDeclaration = 2006,

        /// <summary>A type argument list that is not closed, or closed by a token ending in <c>=</c>.</summary>
        UnclosedTypeArgumentList = 2007,

        /// <summary>A constructor header chaining to something other than <c>super</c> or <c>this</c>.</summary>
        InvalidConstructorChain = 2008,

        /// <summary>A <c>try</c> with neither a <c>catch</c> nor a <c>finally</c>.</summary>
        IncompleteTryStatement = 2009,

        /// <summary>A declaration used as the unbraced body of an <c>if</c> or a loop, where it could never be read.</summary>
        DeclarationAsEmbeddedStatement = 2010,

        /// <summary>A malformed <c>$name</c> or <c>${ ... }</c> inside an interpolated string.</summary>
        InvalidInterpolation = 2011,

        #endregion
    }
}
