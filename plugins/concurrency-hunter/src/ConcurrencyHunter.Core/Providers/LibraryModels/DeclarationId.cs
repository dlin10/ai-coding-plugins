namespace ConcurrencyHunter.Providers.LibraryModels;

/// <summary>A declaration id in the form <c>DocumentationCommentId.CreateDeclarationId</c> writes it (ECMA-334 D.4.2): a type id
/// <c>T:</c>, a member id <c>M:</c>, or the member pattern <c>M:Namespace.Type.Name(*)</c>. <see cref="Parse"/> refuses text that
/// breaks the grammar anywhere, and a parsed id renders back to its text. The member rules on top of the grammar
/// (conversion operators with their return type, the pattern form) are the readers'.</summary>
/// <remarks>A name is a C# identifier, <c>#ctor</c> or <c>#cctor</c>, or one of the metadata names that call writes as they are:
/// an explicit implementation's (<c>System#IDisposable#Dispose</c>, the interface's dots written <c>#</c>), a compiler-generated
/// name (it starts with <c>&lt;</c>), and any name nested in a compiler-generated type. Angle brackets balance in each.</remarks>
/// <param name="Type">The dotted names of the type, namespace first: the type a type id names, or a member id's containing type.</param>
/// <param name="Member">The member, or <c>null</c> for a type id.</param>
internal sealed record DeclarationId(IReadOnlyList<IdName> Type, IdMember? Member)
{
    internal bool IsType => Member is null;
    internal bool IsPattern => Member is { AnyParameters: true };
    internal bool IsMember => Member is { AnyParameters: false };
    internal string TypeName => string.Join('.', Type);

    public override string ToString() => (Member is null ? "T:" : "M:") + TypeName + (Member is null ? "" : "." + Member);

    internal static DeclarationId? Parse(string text) => new Parser(text, unnamedTypes: false).Id();

    /// <summary>Whether an id carries Roslyn's text for a function-pointer type, which it writes as an empty type name: an id that
    /// <see cref="Parse"/> does not give as a member — it refuses it, or reads <c>(*)</c> as the pattern "any parameters" — and that
    /// parses as a member once every empty type name at a type position is read as a type. Such a position is a parameter that is
    /// empty or starts with <c>[</c>, <c>*</c> or <c>@</c> (<c>()</c>, <c>(,</c>, <c>,,</c>, <c>,)</c>, <c>([]</c>, <c>(*</c>,
    /// <c>(@</c>), and a <c>~</c> return that is empty or starts with <c>[</c> or <c>*</c>. <c>()</c> is Roslyn's text for a sole
    /// function-pointer parameter, since an id without parameters has no parentheses. Any other id that does not parse stays refused.</summary>
    /// <param name="id">The text.</param>
    internal static bool HasEmptyTypeSegment(string id) =>
        Parse(id) is not { IsMember: true } && new Parser(id, unnamedTypes: true).Id() is { IsMember: true };

    /// <summary>The place a type stands in, which decides whether an empty name there is a function-pointer type.</summary>
    private enum Position
    {
        /// <summary>A type argument or a type nested in one: a function-pointer type is never one.</summary>
        Argument,

        /// <summary>A parameter of the member.</summary>
        Parameter,

        /// <summary>The member's return type, after <c>~</c>.</summary>
        Return
    }

    /// <summary>Parses an id.</summary>
    /// <param name="text">The id.</param>
    /// <param name="unnamedTypes">Whether an empty type name at a parameter or return position is read as a type, and <c>(*)</c> as
    /// a parameter that is a pointer to one rather than as the pattern "any parameters".</param>
    private sealed class Parser(string text, bool unnamedTypes)
    {
        private const string STRUCTURAL = ".(),{}[]~`@*";
        private int _at;

        internal DeclarationId? Id()
        {
            if (text.Length < 2 || text[0] is not ('T' or 'M') || text[1] != ':')
                return null;
            _at = 2;
            var parts = new List<(string Name, int Arity, bool OfMethod)>();
            do
            {
                WorkCancellation.Check();
                var name = Name();
                var ofMethod = false;
                var arity = 0;
                if (Take('`'))
                {
                    ofMethod = Take('`');
                    if (Number() is not { } count || count == 0)
                        return null;
                    arity = count;
                }
                parts.Add((name, arity, ofMethod));
            } while (Take('.'));

            var typeCount = text[0] == 'T' ? parts.Count : parts.Count - 1;
            var generated = false;
            if (typeCount < 1)
                return null;
            foreach (var part in parts.Take(typeCount))
            {
                WorkCancellation.Check();
                if (!IsDeclaredName(part.Name, ref generated) || part.OfMethod)
                    return null;
            }
            var type = parts.Take(typeCount).Select(part => new IdName(part.Name, part.Arity > 0 ? part.Arity : null, [])).ToArray();
            if (text[0] == 'T')
                return _at == text.Length ? new DeclarationId(type, null) : null;

            var (memberName, memberArity, memberOfMethod) = parts[^1];
            if (memberName is not ("#ctor" or "#cctor") && !IsImplementationName(memberName) && !IsDeclaredName(memberName, ref generated) ||
                memberArity > 0 && !memberOfMethod)
                return null;
            IReadOnlyList<IdParameter>? parameters = null;
            var anyParameters = false;
            if (Take('('))
            {
                if (!unnamedTypes && Take('*'))
                    anyParameters = true;
                else
                {
                    var list = new List<IdParameter>();
                    do
                    {
                        WorkCancellation.Check();
                        if (Type(Position.Parameter) is not { } parameter)
                            return null;
                        list.Add(new IdParameter(parameter, Take('@')));
                    } while (Take(','));
                    parameters = list;
                }
                if (!Take(')'))
                    return null;
            }
            IdType? returnType = null;
            if (!anyParameters && Take('~') && (returnType = Type(Position.Return)) is null)
                return null;
            return _at == text.Length
                ? new DeclarationId(type, new IdMember(memberName, memberArity, parameters, anyParameters, returnType))
                : null;
        }

        private IdType? Type(Position position = Position.Argument)
        {
            WorkCancellation.Check();
            IdType type;
            if (Take('`'))
            {
                var ofMethod = Take('`');
                if (Number() is not { } ordinal)
                    return null;
                type = new IdTypeParameter(ofMethod, ordinal);
            }
            else
            {
                var names = new List<IdName>();
                var generated = false;
                if (unnamedTypes && AtUnnamedType(position))
                    names.Add(new IdName("", null, []));
                else
                {
                    do
                    {
                        WorkCancellation.Check();
                        var name = Name();
                        if (!IsDeclaredName(name, ref generated))
                            return null;
                        int? arity = null;
                        if (Take('`') && (arity = Number()) is null)
                            return null;
                        var arguments = new List<IdType>();
                        if (Take('{'))
                        {
                            do
                            {
                                WorkCancellation.Check();
                                if (Type() is not { } argument)
                                    return null;
                                arguments.Add(argument);
                            } while (Take(','));
                            if (!Take('}'))
                                return null;
                        }
                        names.Add(new IdName(name, arity, arguments));
                    } while (Take('.'));
                }
                type = new IdNamedType(names);
            }
            while (true)
            {
                WorkCancellation.Check();
                if (Take('*'))
                    type = new IdPointerType(type);
                else if (Take('['))
                {
                    var dimensions = new List<string>();
                    do
                    {
                        WorkCancellation.Check();
                        var start = _at;
                        if (Peek(char.IsAsciiDigit) && Number() is null ||
                            Take(':') && Peek(char.IsAsciiDigit) && Number() is null)
                            return null;
                        dimensions.Add(text[start.._at]);
                    } while (Take(','));
                    if (!Take(']'))
                        return null;
                    type = new IdArrayType(type, dimensions);
                }
                else
                    return type;
            }
        }

        /// <summary>Whether the text here is the empty type name of a function-pointer type: nothing stands where the type should, before
        /// what ends a parameter or what a type is wrapped in.</summary>
        /// <param name="position">Where the type stands.</param>
        private bool AtUnnamedType(Position position) => position switch
        {
            Position.Parameter => Peek(character => character is ',' or ')' or '[' or '*' or '@'),
            Position.Return => _at == text.Length || Peek(character => character is '[' or '*'),
            _ => false
        };

        /// <summary>The text up to the next structural character or whitespace; inside angle brackets only a parenthesis, a dot
        /// or whitespace ends it. What the name may be is the caller's check.</summary>
        private string Name()
        {
            var start = _at;
            var depth = 0;
            for (; _at < text.Length && !char.IsWhiteSpace(text[_at]); _at++)
            {
                WorkCancellation.Check();
                var character = text[_at];
                if (depth > 0 ? character is '(' or ')' or '.' : STRUCTURAL.Contains(character))
                    break;
                depth = character switch { '<' => depth + 1, '>' => Math.Max(depth - 1, 0), _ => depth };
            }
            return text[start.._at];
        }

        /// <summary>A number with no leading zero, or null.</summary>
        private int? Number()
        {
            var digits = Run(char.IsAsciiDigit);
            return digits.Length > 0 && (digits == "0" || digits[0] != '0') && int.TryParse(digits, out var value) ? value : null;
        }

        private string Run(Func<char, bool> accepts)
        {
            var start = _at;
            while (_at < text.Length && accepts(text[_at]))
            {
                WorkCancellation.Check();
                _at++;
            }
            return text[start.._at];
        }

        private bool Peek(Func<char, bool> accepts) => _at < text.Length && accepts(text[_at]);

        private bool Take(char expected)
        {
            if (_at >= text.Length || text[_at] != expected)
                return false;
            _at++;
            return true;
        }

        /// <summary>A C# identifier, or a compiler-generated name — one that starts with <c>&lt;</c>, or a name nested in one — whose
        /// angle brackets balance. A nested name that does not start with <c>&lt;</c> has only the characters the compiler writes in
        /// one besides an identifier's, <c>=</c> and <c>$</c> (<c>__StaticArrayInitTypeSize=16</c>). <paramref name="generated"/>
        /// carries over to the names after it.</summary>
        private static bool IsDeclaredName(string name, ref bool generated)
        {
            if (IsIdentifier(name))
                return true;
            generated |= name.StartsWith('<');
            return generated && name.Length > 0 && Balances(name) &&
                   (name.StartsWith('<') || name.All(character => char.IsLetterOrDigit(character) || character is '_' or '=' or '$' or '<' or '>'));
        }

        /// <summary>An explicit implementation's name: the interface's name and the member's, each part an identifier with an
        /// optional <c>global::</c> before it and optional type arguments in angle brackets after it, joined by <c>#</c>.</summary>
        /// <param name="name">The explicit implementation name to check.</param>
        private static bool IsImplementationName(string name)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var index = 0; index < name.Length; index++)
            {
                WorkCancellation.Check();
                depth += name[index] switch { '<' => 1, '>' => -1, _ => 0 };
                if (depth == 0 && name[index] == '#')
                {
                    parts.Add(name[start..index]);
                    start = index + 1;
                }
            }
            parts.Add(name[start..]);
            return parts.Count > 1 && Balances(name) && parts.All(part =>
            {
                var bare = part.StartsWith("global::", StringComparison.Ordinal) ? part[8..] : part;
                var open = bare.IndexOf('<');
                return open < 0 ? IsIdentifier(bare) : IsIdentifier(bare[..open]) && Closes(bare[open..]);
            });
        }

        private static bool IsIdentifier(string name) => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') &&
                                                        name.All(character => char.IsLetterOrDigit(character) || character == '_');

        private static bool Balances(string name)
        {
            var depth = 0;
            foreach (var character in name)
            {
                WorkCancellation.Check();
                if (character == '<')
                    depth++;
                else if (character == '>' && --depth < 0)
                    return false;
            }
            return depth == 0;
        }

        /// <summary>The first angle bracket of <paramref name="text"/> closes at its last character.</summary>
        /// <param name="text">The text beginning with the opening angle bracket.</param>
        private static bool Closes(string text)
        {
            var depth = 0;
            for (var index = 0; index < text.Length; index++)
            {
                WorkCancellation.Check();
                depth += text[index] switch { '<' => 1, '>' => -1, _ => 0 };
                if (depth <= 0)
                    return depth == 0 && index == text.Length - 1;
            }
            return false;
        }
    }
}

/// <summary>A namespace or type name with its generic arity (<c>`N</c>) or type arguments (<c>{…}</c>). A type reference writes
/// the arity of a generic definition it names from inside, <c>`0</c> included.</summary>
internal sealed record IdName(string Name, int? Arity, IReadOnlyList<IdType> Arguments)
{
    public override string ToString() => Name + (Arity is { } arity ? "`" + arity : "") +
                                         (Arguments.Count > 0 ? "{" + string.Join(',', Arguments) + "}" : "");
}

/// <summary>A member name with its method arity, its parameters (<c>(*)</c> for a pattern), and the return type after <c>~</c>.</summary>
internal sealed record IdMember(string Name, int Arity, IReadOnlyList<IdParameter>? Parameters, bool AnyParameters, IdType? ReturnType)
{
    public override string ToString() => Name + (Arity > 0 ? "``" + Arity : "") +
                                         (AnyParameters ? "(*)" : Parameters is null ? "" : "(" + string.Join(',', Parameters) + ")") +
                                         (ReturnType is null ? "" : "~" + ReturnType);
}

internal sealed record IdParameter(IdType Type, bool ByReference)
{
    public override string ToString() => Type + (ByReference ? "@" : "");
}

internal abstract record IdType;

internal sealed record IdNamedType(IReadOnlyList<IdName> Names) : IdType
{
    public override string ToString() => string.Join('.', Names);
}

internal sealed record IdTypeParameter(bool OfMethod, int Ordinal) : IdType
{
    public override string ToString() => (OfMethod ? "``" : "`") + Ordinal;
}

internal sealed record IdArrayType(IdType Element, IReadOnlyList<string> Dimensions) : IdType
{
    public override string ToString() => Element + "[" + string.Join(',', Dimensions) + "]";
}

internal sealed record IdPointerType(IdType Pointed) : IdType
{
    public override string ToString() => Pointed + "*";
}
