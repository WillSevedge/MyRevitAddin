using System.Text.Json;

namespace Nexus.Contracts;

/// <summary>
/// The AutoCAD tools for AI assistants: names, descriptions and argument schemas, in one place for both sides. The
/// AutoCAD add-in implements them; Nexus.Mcp lists them even before AutoCAD runs (the assistant then learns them up
/// front, and is told to open AutoCAD when it calls one).
/// </summary>
public static class CadToolCatalog
{
    private const string SpaceProperty =
        "\"space\": {\"type\": \"string\", \"description\": \"\\\"model\\\" (default) or a layout name for paper space.\"}";

    private const string HandlesProperty =
        "\"handles\": {\"type\": \"array\", \"items\": {\"type\": \"string\"}, \"description\": \"Entity handles (hexadecimal) from query_entities, get_selection or create_entities.\"}";

    private const string ItemSchema =
        "{\"type\": \"object\", \"properties\": {" +
        "\"type\": {\"type\": \"string\", \"enum\": [\"line\", \"polyline\", \"rectangle\", \"circle\", \"arc\", \"point\", \"text\", \"mtext\", \"block\", \"dimension\"]}, " +
        "\"layer\": {\"type\": \"string\"}, \"color\": {\"type\": \"string\", \"description\": \"ByLayer (default), 1-255, red, \\\"r,g,b\\\"…\"}, \"linetype\": {\"type\": \"string\"}, " +
        "\"start\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"description\": \"line, dimension: first point [x, y]\"}, " +
        "\"end\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"description\": \"line, dimension: second point\"}, " +
        "\"points\": {\"type\": \"array\", \"items\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}}, \"description\": \"polyline vertices\"}, " +
        "\"bulges\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"description\": \"polyline: bulge per vertex (0 = straight)\"}, " +
        "\"closed\": {\"type\": \"boolean\"}, \"width\": {\"type\": \"number\", \"description\": \"polyline constant width\"}, " +
        "\"corner1\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}}, \"corner2\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"description\": \"rectangle: opposite corners\"}, " +
        "\"center\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}}, \"radius\": {\"type\": \"number\"}, " +
        "\"start_angle\": {\"type\": \"number\", \"description\": \"arc, degrees counter-clockwise from +X\"}, \"end_angle\": {\"type\": \"number\"}, " +
        "\"position\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"description\": \"point, text, mtext, block insertion point\"}, " +
        "\"text\": {\"type\": \"string\"}, \"height\": {\"type\": \"number\", \"description\": \"text height (default: TEXTSIZE)\"}, " +
        "\"rotation\": {\"type\": \"number\", \"description\": \"degrees\"}, \"mtext_width\": {\"type\": \"number\"}, " +
        "\"name\": {\"type\": \"string\", \"description\": \"block: block definition name (see list_blocks)\"}, \"scale\": {\"type\": \"number\"}, " +
        "\"attributes\": {\"type\": \"object\", \"additionalProperties\": {\"type\": \"string\"}, \"description\": \"block: TAG → value\"}, " +
        "\"dimension_line\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"description\": \"dimension: a point the dimension line passes through\"}}, " +
        "\"required\": [\"type\"]}";

    public static IReadOnlyList<ToolDescriptor> All { get; } = Build();

    public static ToolDescriptor Get(string name) =>
        All.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException($"No CAD tool '{name}'.", nameof(name));

    private static List<ToolDescriptor> Build() => new()
    {
        Tool("drawing_info", "Drawing info",
        "Overview of the open drawing: file, units, extents, layouts, the current layout, how many entities of each type are in model space, layers, blocks and xrefs. Start here.",
        "{\"type\": \"object\", \"properties\": {}}", readOnly: true),
        Tool("list_layers", "List layers",
        "Every layer with its colour, linetype, on/frozen/locked/plot state and how many model-space entities use it.",
        "{\"type\": \"object\", \"properties\": {\"name\": {\"type\": \"string\", \"description\": \"Only layers matching this (wildcards * and ?).\"}}}",
        readOnly: true),
        Tool("list_blocks", "List blocks",
        "Block definitions in the drawing: name, attribute tags, whether dynamic or an xref, and how many references are placed.",
        "{\"type\": \"object\", \"properties\": {\"name\": {\"type\": \"string\", \"description\": \"Only blocks matching this (wildcards).\"}, \"include_xrefs\": {\"type\": \"boolean\", \"default\": true}}}",
        readOnly: true),
        Tool("query_entities", "Find entities",
        "Find entities in model space or a layout, filtered by type, layer, block name, text and area. Returns a page of compact results " +
        "(handle, type, layer, key geometry/text, bounding box) and the total count; use offset to page. Types: line, polyline, circle, arc, " +
        "ellipse, spline, point, text, mtext, block, dimension, leader, hatch, table, viewport, solid, image, or DXF names.",
        "{\"type\": \"object\", \"properties\": {" + SpaceProperty + ", " +
        "\"types\": {\"type\": \"array\", \"items\": {\"type\": \"string\"}}, " +
        "\"layers\": {\"type\": \"array\", \"items\": {\"type\": \"string\"}, \"description\": \"Layer names, wildcards * and ? allowed.\"}, " +
        "\"blocks\": {\"type\": \"array\", \"items\": {\"type\": \"string\"}, \"description\": \"Block names (for block references), wildcards allowed.\"}, " +
        "\"text_contains\": {\"type\": \"string\", \"description\": \"Text, mtext, attributes or dimension text containing this (case-insensitive).\"}, " +
        "\"window\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"minItems\": 4, \"maxItems\": 4, \"description\": \"[minX, minY, maxX, maxY]: entities whose bounding box touches this area.\"}, " +
        "\"limit\": {\"type\": \"integer\", \"default\": 100, \"maximum\": 1000}, \"offset\": {\"type\": \"integer\", \"default\": 0}}}",
        readOnly: true),
        Tool("get_entities", "Entity details",
        "Full details of entities by handle: every vertex, bulges, attributes, dynamic block properties, text contents, style, linetype.",
        "{\"type\": \"object\", \"properties\": {" + HandlesProperty + "}, \"required\": [\"handles\"]}", readOnly: true),
        Tool("get_selection", "Current selection",
        "The entities the user has selected (gripped) in AutoCAD right now. Use this when the user says \"these\", \"the selected\", \"what I picked\".",
        "{\"type\": \"object\", \"properties\": {}}", readOnly: true),
        Tool("view_image", "Picture of the view",
        "A PNG of what AutoCAD's drawing area shows now (the current drawing becomes the active one). Use zoom first to frame what you want to check.",
        "{\"type\": \"object\", \"properties\": {\"width\": {\"type\": \"integer\", \"default\": 1280, \"maximum\": 2048}, \"height\": {\"type\": \"integer\", \"default\": 800, \"maximum\": 2048}}}",
        readOnly: true),
        Tool("create_entities", "Draw entities",
        "Draw one or more entities in model space (or a layout) in one undo step. Coordinates are drawing units, angles degrees. " +
        "Missing layers are created. Returns the new handles. Check the result with view_image.",
        "{\"type\": \"object\", \"properties\": {" + SpaceProperty + ", " +
        "\"create_layers\": {\"type\": \"boolean\", \"default\": true, \"description\": \"Create layers that do not exist yet.\"}, " +
        "\"items\": {\"type\": \"array\", \"items\": " + ItemSchema + "}}, \"required\": [\"items\"]}",
        readOnly: false),
        Tool("modify_entities", "Change entities",
        "Change entities by handle, in one undo step: layer, color, linetype, move by [dx, dy], rotate (degrees about a base point), scale (factor about a base point), " +
        "text (text/mtext) and block attribute values. Only the properties given change.",
        "{\"type\": \"object\", \"properties\": {" + HandlesProperty + ", " +
        "\"layer\": {\"type\": \"string\"}, \"color\": {\"type\": \"string\"}, \"linetype\": {\"type\": \"string\"}, " +
        "\"move\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"description\": \"[dx, dy] or [dx, dy, dz]\"}, " +
        "\"rotate\": {\"type\": \"number\", \"description\": \"degrees counter-clockwise\"}, \"scale\": {\"type\": \"number\"}, " +
        "\"base_point\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"description\": \"for rotate and scale (default: the centre of the entities)\"}, " +
        "\"text\": {\"type\": \"string\"}, \"attributes\": {\"type\": \"object\", \"additionalProperties\": {\"type\": \"string\"}}, " +
        "\"create_layers\": {\"type\": \"boolean\", \"default\": true}}, \"required\": [\"handles\"]}",
        readOnly: false),
        Tool("delete_entities", "Delete entities",
        "Erase entities by handle (one undo step). Entities on locked layers are skipped and reported.",
        "{\"type\": \"object\", \"properties\": {" + HandlesProperty + "}, \"required\": [\"handles\"]}",
        readOnly: false, destructive: true),
        Tool("set_layer", "Create or change a layer",
        "Create a layer, or change one: color, linetype, description, on, frozen, locked, plot; make_current sets it as the current layer.",
        "{\"type\": \"object\", \"properties\": {\"name\": {\"type\": \"string\"}, \"color\": {\"type\": \"string\"}, \"linetype\": {\"type\": \"string\"}, " +
        "\"description\": {\"type\": \"string\"}, \"on\": {\"type\": \"boolean\"}, \"frozen\": {\"type\": \"boolean\"}, \"locked\": {\"type\": \"boolean\"}, " +
        "\"plot\": {\"type\": \"boolean\"}, \"make_current\": {\"type\": \"boolean\"}}, \"required\": [\"name\"]}",
        readOnly: false),
        Tool("zoom", "Zoom",
        "Frame entities (handles), an area ([minX, minY, maxX, maxY]) or the whole drawing (extents: true) in AutoCAD's view. Selects the entities when handles are given.",
        "{\"type\": \"object\", \"properties\": {" + HandlesProperty + ", \"window\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}}, \"extents\": {\"type\": \"boolean\"}}}",
        readOnly: true),
        Tool("run_command", "Run a command",
        "Last resort, only when no other autocad_ tool can do it (never to draw or change entities). Types a command or AutoLISP expression on AutoCAD's command line, e.g. \"_.REGEN\", \"_.PURGE _A * _N\", \"(command \\\"_.LAYOFF\\\" ...)\". " +
        "Use English command names with the _. prefix and give every prompt's answer (a space or newline ends input). It runs after this call " +
        "returns: check the outcome with drawing_info, query_entities or view_image. Prefer the specific tools when one fits.",
        "{\"type\": \"object\", \"properties\": {\"command\": {\"type\": \"string\"}}, \"required\": [\"command\"]}",
        readOnly: false, destructive: true),
    };

    private static ToolDescriptor Tool(string name, string title, string description, string schema, bool readOnly, bool destructive = false)
    {
        using var doc = JsonDocument.Parse(schema);
        return new ToolDescriptor
        {
            Name = name, Title = title, Description = description, InputSchema = doc.RootElement.Clone(),
            ReadOnly = readOnly, Destructive = destructive,
        };
    }
}
