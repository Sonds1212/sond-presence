namespace SondPresence;

/// <summary>
/// Itens da loja do client, pagos com coins. Por enquanto só cores do ícone (o selo da preguiça);
/// capas e chapéus entram aqui quando existirem. O id nunca muda depois de publicado — é ele que
/// fica no histórico de compras.
/// </summary>
public static class ShopCatalog
{
    public sealed record Item(string Id, string Name, string Kind, int Color, long Price);

    public static readonly IReadOnlyList<Item> Items =
    [
        new("color_snow", "Neve", "color", 0xE8F1FF, 50),
        new("color_mint", "Menta", "color", 0x5CF2B4, 100),
        new("color_sky", "Céu", "color", 0x5AB4FF, 100),
        new("color_lemon", "Limão", "color", 0xE6F25C, 100),
        new("color_tangerine", "Tangerina", "color", 0xFF9A3C, 150),
        new("color_bubblegum", "Chiclete", "color", 0xFF7AC8, 150),
        new("color_grape", "Uva", "color", 0x9B5CFF, 200),
        new("color_ruby", "Rubi", "color", 0xE0263F, 250),
        new("color_ocean", "Oceano", "color", 0x1E6BFF, 250),
        new("color_emerald", "Esmeralda", "color", 0x10B060, 300),
        new("color_gold", "Ouro", "color", 0xFFC93C, 400),
        new("color_midnight", "Meia-noite", "color", 0x23234A, 500),
    ];

    public static Item? Find(string? id) =>
        id is null ? null : Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
}
