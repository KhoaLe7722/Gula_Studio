using System.Text.Json;
using System.Text.RegularExpressions;
using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.AI;

public interface IOutfitGalleryService
{
    bool IsAvailable { get; }
    void Prewarm();
    OutfitGalleryResponse Query(bool includeNsfw, string? gender, string? collection, string? category, string? query, int page, int pageSize);
    bool TryResolveImageFile(string outfitId, string kind, string view, out string fullPath, out string contentType);
    bool OutfitExists(string outfitId);
    string BuildSynchronizedPrompt(string outfitId, string basePrompt);
}

public sealed class OutfitGalleryService : IOutfitGalleryService
{
    private readonly string _rootDir;
    private readonly string _thumbsRoot;
    private readonly string _imgRoot;
    private readonly ILogger<OutfitGalleryService> _logger;
    private readonly object _loadLock = new();
    private bool _loaded;
    private List<OutfitEntry> _entries = [];
    private List<OutfitEntry> _sfwEntries = [];
    private Dictionary<string, OutfitEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private List<PoseGalleryFilterOption> _allGenderOptions = [];
    private List<PoseGalleryFilterOption> _sfwGenderOptions = [];

    private static readonly string[] ShiseiViewOrder = ["front", "diag", "side", "back"];

    private static readonly Dictionary<string, int> GenderOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["male"] = 1,
        ["unisex"] = 2,
        ["female"] = 3,
    };

    // Empirically calibrated from live RTX 4050 Z-Image-Turbo benchmarks
    private static readonly Dictionary<string, int> CategoryReliabilityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shisei_md"] = 1,
        ["shisei_fm"] = 2,
        ["men_jacket"] = 3,
        ["men_suit"] = 4,
        ["men_street"] = 5,
        ["men_knit"] = 6,
        ["men_shirt"] = 7,
        ["men_casual"] = 8,
        ["autumn"] = 9,
        ["summer"] = 10,
        ["spring"] = 11,
        ["shisei_fn"] = 12,
        ["shisei_sp"] = 13,
        ["shisei_wk"] = 14,
        ["shisei_hc"] = 15,
        ["shisei_we"] = 16,
        ["shisei_wh"] = 17,
        ["winter"] = 18,
        ["party"] = 19,
        ["swim"] = 20,
        ["shisei_fs"] = 21,
        ["shisei_in"] = 22,
        ["lingerie"] = 23,
        ["sexy"] = 24,
        ["shisei_lg"] = 25,
    };

    private static readonly Dictionary<string, (string Key, string LabelVi, bool IsNsfw)> ModernCategoryMap = new()
    {
        ["春季穿搭"] = ("spring", "Xuân · Thanh lịch", false),
        ["夏季穿搭"] = ("summer", "Hè · Năng động", false),
        ["秋季穿搭"] = ("autumn", "Thu · Phối lớp", false),
        ["冬季穿搭"] = ("winter", "Đông · Áo khoác", false),
        ["派对・礼服"] = ("party", "Dự tiệc · Lễ hội", false),
        ["泳装・度假"] = ("swim", "Đồ bơi · Nghỉ dưỡng", false),
        ["性感穿搭"] = ("sexy", "Gợi cảm (18+)", true),
        ["内衣・睡衣"] = ("lingerie", "Nội y · Đồ ngủ (18+)", true),
    };

    private static readonly Dictionary<string, (string Key, string LabelVi, string CollectionKey, string CollectionLabelVi, bool IsNsfw)> ShiseiCatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MD"] = ("shisei_md", "Thời trang Hiện đại", "shisei_modern", "Hiện đại · Đa phong cách (CC0)", false),
        ["FM"] = ("shisei_fm", "Vest · Lễ phục", "shisei_modern", "Hiện đại · Đa phong cách (CC0)", false),
        ["SP"] = ("shisei_sp", "Thể thao · Dã ngoại", "shisei_modern", "Hiện đại · Đa phong cách (CC0)", false),
        ["WK"] = ("shisei_wk", "Đồng phục · Nghề nghiệp", "shisei_modern", "Hiện đại · Đa phong cách (CC0)", false),
        ["IN"] = ("shisei_in", "Đồ ngủ · Mặc nhà · Bơi", "shisei_modern", "Hiện đại · Đa phong cách (CC0)", false),
        ["FN"] = ("shisei_fn", "Giả tưởng · Kiếm hiệp · Sci-Fi", "shisei_fantasy", "Giả tưởng & Cổ trang (CC0)", false),
        ["HC"] = ("shisei_hc", "Cổ trang Trung Hoa", "shisei_fantasy", "Giả tưởng & Cổ trang (CC0)", false),
        ["WE"] = ("shisei_we", "Truyền thống Thế giới (Áo dài…)", "shisei_fantasy", "Giả tưởng & Cổ trang (CC0)", false),
        ["WH"] = ("shisei_wh", "Cổ điển Phương Tây", "shisei_fantasy", "Giả tưởng & Cổ trang (CC0)", false),
        ["FS"] = ("shisei_fs", "Lễ hội · Biểu diễn", "shisei_fantasy", "Giả tưởng & Cổ trang (CC0)", false),
        ["LG"] = ("shisei_lg", "Gợi cảm · Nội y CC0 (18+)", "shisei_modern", "Hiện đại · Đa phong cách (CC0)", true),
    };

    private static readonly Dictionary<string, string> ShiseiEraMapVi = new(StringComparer.OrdinalIgnoreCase)
    {
        ["casual"] = "Thường ngày", ["street"] = "Đường phố", ["commute"] = "Công sở", ["minimal"] = "Tối giản",
        ["korean"] = "Hàn Quốc", ["japanese"] = "Nhật Bản", ["americana"] = "Mỹ cổ điển", ["french"] = "Pháp thanh lịch",
        ["gorpcore"] = "Gorpcore", ["techwear"] = "Techwear", ["preppy"] = "Học đường", ["mori"] = "Mori tự nhiên",
        ["newchinese"] = "Tân Trung Hoa", ["resort"] = "Nghỉ dưỡng", ["sweet"] = "Ngọt ngào", ["y2k"] = "Y2K",
        ["suits"] = "Vest · Suit", ["menformal"] = "Lễ phục Nam", ["evening"] = "Dạ hội", ["chinese"] = "Lễ phục Á Đông",
        ["academic"] = "Lễ phục Tốt nghiệp", ["wedding"] = "Cưới",
        ["knight"] = "Hiệp sĩ", ["mage"] = "Pháp sư", ["assassin"] = "Sát thủ", ["bard"] = "Thi sĩ",
        ["cyberpunk"] = "Cyberpunk", ["steampunk"] = "Steampunk", ["wasteland"] = "Hậu tận thế",
        ["wuxia"] = "Kiếm hiệp", ["xianxia"] = "Tiên hiệp", ["mecha-pilot"] = "Phi công Mecha", ["northern"] = "Bắc Âu",
        ["ball-sports"] = "Bóng đá · Bóng rổ", ["studio"] = "Gym · Tập luyện", ["endurance"] = "Chạy bộ · Đạp xe",
        ["outdoor"] = "Leo núi · Dã ngoại", ["snow"] = "Trượt tuyết", ["water"] = "Thể thao nước", ["combat"] = "Võ thuật",
        ["medical"] = "Bác sĩ · Y tế", ["food"] = "Đầu bếp · Pha chế", ["transport"] = "Phi công · Hàng không",
        ["trades"] = "Kỹ thuật · Công xưởng", ["uniform"] = "Đồng phục", ["service"] = "Dịch vụ",
        ["shang"] = "Nhà Thương", ["han"] = "Nhà Hán", ["weijin"] = "Ngụy Tấn", ["tang"] = "Nhà Đường",
        ["song"] = "Nhà Tống", ["yuan"] = "Nhà Nguyên", ["ming"] = "Nhà Minh", ["qing"] = "Nhà Thanh", ["rc"] = "Dân Quốc",
        ["seasia"] = "Đông Nam Á · Việt Nam", ["eastasia"] = "Đông Á (Nhật · Hàn)", ["europe"] = "Châu Âu",
        ["africa"] = "Trung Đông · Châu Phi", ["sasia"] = "Nam Á", ["cnminority"] = "Dân tộc thiểu số",
        ["sleep"] = "Đồ ngủ", ["lounge"] = "Mặc nhà", ["robe"] = "Áo choàng tắm", ["swim"] = "Đồ bơi",
        ["lingerie"] = "Nội y", ["roleplay"] = "Hóa trang", ["performer"] = "Biểu diễn", ["spring-festival"] = "Tết · Lễ hội",
    };

    private static readonly Dictionary<string, string> ViewLabelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["front"] = "Chính diện",
        ["diag"] = "Nghiêng 3/4",
        ["side"] = "Góc ngang",
        ["back"] = "Phía sau",
    };

    private static readonly Dictionary<string, string> TagMapVi = new()
    {
        ["象牙白"] = "Trắng ngà", ["奶油色"] = "Màu kem", ["白色"] = "Trắng", ["黑色"] = "Đen",
        ["灰色"] = "Xám", ["米色"] = "Màu be", ["棕色"] = "Nâu", ["卡其色"] = "Kaki",
        ["红色"] = "Đỏ", ["酒红色"] = "Đỏ rượu", ["粉色"] = "Hồng", ["橙色"] = "Cam",
        ["黄色"] = "Vàng", ["绿色"] = "Xanh lá", ["橄榄绿"] = "Xanh ô-liu", ["薄荷绿"] = "Xanh bạc hà",
        ["蓝色"] = "Xanh dương", ["藏青色"] = "Xanh navy", ["紫色"] = "Tím", ["薰衣草紫"] = "Tím oải hương",
        ["金色"] = "Ánh vàng", ["银色"] = "Ánh bạc",
        ["连衣裙"] = "Váy liền", ["半身裙"] = "Chân váy", ["迷你"] = "Dáng ngắn", ["中长"] = "Dáng lửng", ["长款"] = "Dáng dài",
        ["百褶"] = "Xếp ly", ["荷叶边"] = "Viền bèo", ["蝴蝶结"] = "Nơ", ["碎花"] = "Hoa nhí", ["波点"] = "Chấm bi",
        ["针织"] = "Len dệt kim", ["毛衣"] = "Áo len", ["开衫"] = "Cardigan", ["吊带衫"] = "Áo hai dây",
        ["衬衫"] = "Sơ mi", ["女衫"] = "Áo kiểu", ["高领"] = "Cổ lọ", ["连帽卫衣"] = "Hoodie",
        ["马甲"] = "Áo gile", ["罩衫"] = "Áo choàng nhẹ", ["夹克"] = "Áo khoác ngắn", ["大衣"] = "Áo khoác dài",
        ["羊毛"] = "Len lông cừu", ["毛绒"] = "Lông mềm", ["皮质"] = "Chất da", ["麂皮"] = "Da lộn",
        ["牛仔"] = "Denim", ["牛仔裤"] = "Quần jean", ["长裤"] = "Quần dài", ["短裤"] = "Quần short",
        ["蕾丝"] = "Ren", ["雪纺"] = "Voan", ["缎面"] = "Lụa satin", ["丝绒"] = "Nhung",
        ["薄纱"] = "Vải sheer", ["网纱"] = "Vải lưới", ["灯芯绒"] = "Nhung tăm",
        ["泳衣"] = "Đồ bơi", ["束腰胸衣"] = "Corset", ["文胸"] = "Áo lót", ["内裤"] = "Quần lót", ["围裙"] = "Tạp dề",
        ["高跟鞋"] = "Giày cao gót", ["靴子"] = "Bốt", ["凉鞋"] = "Sandal", ["运动鞋"] = "Sneaker",
        ["乐福鞋"] = "Giày lười", ["浅口单鞋"] = "Giày búp bê",
        ["短袜"] = "Tất ngắn", ["长筒丝袜"] = "Tất đùi", ["连裤袜"] = "Quần tất",
        ["包"] = "Túi xách", ["托特包"] = "Túi tote", ["腰带"] = "Thắt lưng", ["围巾"] = "Khăn quàng",
        ["丝巾"] = "Khăn lụa", ["手套"] = "Găng tay", ["贝雷帽"] = "Mũ nồi", ["礼帽"] = "Mũ rộng vành",
        ["针织冷帽"] = "Mũ len", ["发箍"] = "Băng đô", ["墨镜"] = "Kính râm", ["项链"] = "Dây chuyền", ["颈链"] = "Vòng choker",
        ["商"] = "Nhà Thương", ["汉"] = "Nhà Hán", ["魏晋"] = "Ngụy Tấn", ["唐"] = "Nhà Đường",
        ["宋"] = "Nhà Tống", ["元"] = "Nhà Nguyên", ["明"] = "Nhà Minh", ["清"] = "Nhà Thanh",
        ["民国"] = "Dân Quốc", ["婚服"] = "Hỷ phục", ["女装"] = "Nữ", ["男装"] = "Nam",
    };

    private static readonly (string Zh, string Vi)[] GarmentTranslations =
    [
        ("荷叶边针织衫", "áo len viền bèo"), ("针织开衫", "áo cardigan len"), ("针织马甲", "áo gile len"),
        ("针织衫", "áo len"), ("针织冷帽", "mũ len"), ("毛绒夹克", "áo khoác lông"), ("毛绒大衣", "áo khoác lông dài"),
        ("羊毛大衣", "áo khoác dạ len"), ("牛仔夹克", "áo khoác jean"), ("皮质夹克", "áo khoác da"),
        ("碎花吊带衫", "áo hai dây hoa nhí"), ("蕾丝吊带衫", "áo hai dây ren"), ("吊带迷你连衣裙", "váy hai dây ngắn"),
        ("吊带连衣裙", "váy hai dây"), ("吊带衫", "áo hai dây"), ("衬衫裙", "váy sơ mi"), ("女衫", "áo kiểu"), ("衬衫", "áo sơ mi"),
        ("高领迷你连衣裙", "váy cổ lọ ngắn"), ("高领衫", "áo cổ lọ"), ("连帽卫衣", "áo hoodie"), ("毛衣", "áo len dày"),
        ("开衫", "áo cardigan"), ("夹克", "áo khoác ngắn"), ("大衣", "áo khoác dài"), ("马甲", "áo gile"), ("罩衫", "áo khoác mỏng"),
        ("百褶迷你半身裙", "chân váy xếp ly ngắn"), ("百褶中长半身裙", "chân váy xếp ly lửng"), ("牛仔迷你半身裙", "chân váy jean ngắn"),
        ("迷你半身裙", "chân váy ngắn"), ("中长半身裙", "chân váy lửng"), ("半身裙", "chân váy"),
        ("迷你裙裤", "chân váy quần ngắn"), ("迷你连衣裙", "váy liền ngắn"), ("连衣裙", "váy liền"),
        ("牛仔长裤", "quần jean dài"), ("牛仔裤", "quần jean"), ("长裤", "quần dài"), ("短裤", "quần short"),
        ("内衣套装", "bộ nội y"), ("束腰胸衣", "áo corset"), ("文胸", "áo bralette"), ("泳衣", "đồ bơi"),
        ("蕾丝长筒丝袜", "tất đùi ren"), ("薄纱长筒丝袜", "tất đùi sheer"), ("长筒丝袜", "tất đùi"),
        ("连裤袜", "quần tất"), ("蕾丝短袜", "tất ren"), ("短袜", "tất ngắn"),
        ("缎面高跟鞋", "giày cao gót satin"), ("高跟鞋", "giày cao gót"), ("皮质靴子", "bốt da"), ("麂皮靴子", "bốt da lộn"), ("靴子", "bốt"),
        ("白色运动鞋", "sneaker trắng"), ("运动鞋", "giày sneaker"), ("乐福鞋", "giày lười loafer"),
        ("浅口单鞋", "giày búp bê"), ("凉鞋", "sandal"), ("迷你托特包", "túi tote mini"), ("托特包", "túi tote"),
        ("迷你包", "túi mini"), ("皮质包", "túi da"), ("网纱包", "túi lưới"), ("包", "túi xách"),
        ("丝绒蝴蝶结", "nơ nhung"), ("缎面蝴蝶结", "nơ satin"), ("蝴蝶结", "nơ"), ("荷叶边", "viền bèo"), ("百褶", "xếp ly"),
        ("蕾丝饰边", "viền ren"), ("蕾丝", "ren"), ("薄纱", "vải sheer"), ("网纱", "lưới"), ("雪纺", "voan"),
        ("缎面", "satin"), ("丝绒", "nhung"), ("灯芯绒", "nhung tăm"), ("麂皮", "da lộn"), ("皮质", "da"),
        ("羊毛", "dạ len"), ("毛绒", "lông mềm"), ("针织", "len"), ("碎花", "hoa nhí"), ("波点", "chấm bi"),
        ("金色项链", "dây chuyền vàng"), ("银色项链", "dây chuyền bạc"), ("项链", "dây chuyền"), ("颈链", "choker"),
        ("丝巾", "khăn lụa"), ("围巾", "khăn quàng"), ("手套", "găng tay"), ("腰带", "thắt lưng"),
        ("贝雷帽", "mũ nồi"), ("礼帽", "mũ rộng vành"), ("发箍", "băng đô"), ("墨镜", "kính râm"),
        ("配饰", "phụ kiện"), ("饰品", "trang sức"),
        ("象牙白", "trắng ngà "), ("奶油色", "màu kem "), ("酒红色", "đỏ rượu "), ("薄荷绿", "xanh bạc hà "),
        ("橄榄绿", "xanh ô-liu "), ("薰衣草紫", "tím oải hương "), ("藏青色", "xanh navy "), ("卡其色", "kaki "),
        ("白色", "trắng "), ("黑色", "đen "), ("灰色", "xám "), ("米色", "màu be "), ("棕色", "nâu "),
        ("红色", "đỏ "), ("粉色", "hồng "), ("橙色", "cam "), ("黄色", "vàng "), ("绿色", "xanh lá "),
        ("蓝色", "xanh dương "), ("紫色", "tím "), ("金色", "ánh vàng "), ("银色", "ánh bạc "),
        ("牛仔", "denim "), ("高领", "cổ lọ "), ("束腰", "chiết eo "), ("迷你", "ngắn "), ("长款", "dáng dài "),
        // Shisei modern, formal, fantasy, sports, workwear & historical terms
        ("奥黛", "Áo Dài Việt Nam"), ("男士三件套西装", "Vest nam 3 mảnh"), ("男士双排扣西装", "Vest nam 2 hàng khuy"),
        ("无尾礼服", "Tuxedo dạ tiệc"), ("燕尾服", "Áo đuôi tôm White-Tie"), ("晨礼服", "Lễ phục Morning Coat"),
        ("新中式立领礼服", "Vest cổ đứng Tân Trung Hoa"), ("学位袍", "Áo cử nhân"),
        ("骑士板甲", "Giáp sắt Hiệp sĩ"), ("法师长袍", "Áo choàng Pháp sư"), ("刺客", "Trang phục Sát thủ"),
        ("吟游诗人", "Trang phục Thi sĩ"), ("赛博朋克街头装", "Streetwear Cyberpunk"), ("蒸汽朋克工程师", "Kỹ sư Steampunk"),
        ("废土拾荒者", "Phong cách Hậu tận thế"), ("机甲驾驶员", "Phi công Mecha"),
        ("医生白大褂", "Áo blouse Bác sĩ"), ("手术服", "Đồ phẫu thuật"), ("厨师服", "Đồng phục Đầu bếp"),
        ("咖啡师围裙装", "Đồng phục Barista"), ("餐厅侍应", "Phục vụ nhà hàng"), ("机长制服", "Đồng phục Phi công"),
        ("篮球服", "Đồ bóng rổ"), ("足球服", "Đồ bóng đá"), ("高尔夫球装", "Đồ đánh Golf"),
        ("健身训练服", "Đồ tập Gym"), ("跑步服", "Đồ chạy bộ"), ("公路骑行服", "Đồ đạp xe"),
        ("单板滑雪服", "Đồ trượt tuyết"), ("登山冲锋衣", "Áo khoác leo núi"), ("冲浪连体服", "Đồ lướt sóng"),
        ("男士沙滩泳裤", "Quần bơi nam"), ("男士棉睡衣", "Đồ ngủ cotton nam"), ("法兰绒格纹睡衣", "Đồ ngủ kẻ caro"),
        ("男士华夫格家居服", "Bộ mặc nhà nam"), ("卫衣家居套装", "Bộ nỉ mặc nhà"), ("毛巾浴袍", "Áo choàng tắm"),
        ("男士丝缎睡袍套装", "Áo choàng ngủ lụa nam"), ("男士内衣套装", "Bộ nội y nam"),
        ("工装裤", "quần túi hộp cargo"), ("阔腿牛仔裤", "quần jean ống rộng"), ("卡其裤", "quần kaki chino"),
        ("阔腿裤", "quần ống rộng"), ("褶裥西裤", "quần âu xếp ly"), ("西裤", "quần tây"), ("运动长裤", "quần thể thao"),
        ("卫衣叠穿", "áo nỉ phối lớp"), ("卫衣", "áo nỉ"), ("面包服", "áo phao ngắn"), ("教练夹克", "áo khoác coach"),
        ("无衬西装", "áo blazer mềm"), ("宽松西装", "blazer dáng rộng"), ("单排扣大衣", "áo măng tô 1 hàng khuy"),
        ("短大衣", "áo khoác dạ ngắn"), ("高领毛衣", "áo len cổ lọ"), ("圆领毛衣", "áo len cổ tròn"), ("方廓衬衫", "áo sơ mi boxy"),
        ("抽绳短裤", "quần short dây rút"), ("宽松T恤", "áo thun oversize"), ("针织Polo", "áo polo dệt kim"),
        ("齐胸襦裙", "Váy Tề Hung Nhu Quần"), ("上衣下裳", "Thượng Y Hạ Thường"), ("直裾深衣", "Trực Cư Thâm Y"),
        ("曲裾深衣", "Khúc Cư Thâm Y"), ("马面裙", "Váy Mã Diện"), ("交领短袄", "Áo Đoản Áo giao lĩnh"),
        ("交领短衫", "Áo Đoản Sam giao lĩnh"), ("交领襦裙", "Nhu Quần giao lĩnh"), ("立领长袄", "Áo Trường Áo cổ đứng"),
        ("倒大袖旗袍", "Sườn xám tay loe cổ điển"), ("旗袍", "Sườn xám"), ("长衫", "Áo Trường Sam"),
        ("圆领袍", "Áo bào cổ tròn"), ("道袍", "Đạo bào"), ("飞鱼服", "Phi Ngư Phục"), ("氅衣", "Áo Sưởng Y"),
        ("褙子", "Áo Bối Tử"), ("披风", "Áo choàng Phi Phong"), ("蔽膝", "Tế Tất"), ("婚服", "Hỷ phục"),
        ("和服", "Kimono Nhật Bản"), ("韩服", "Hanbok Hàn Quốc"), ("马来装", "Baju Melayu"),
        ("炭灰", "xám than"), ("藏青", "xanh navy"), ("午夜蓝", "xanh đêm"), ("驼色", "màu lạc đà"),
        ("燕麦", "màu yến mạch"), ("靛蓝", "xanh chàm"), ("本白", "trắng kem"), ("麻灰", "xám muối tiêu"),
        ("玄黑", "đen huyền"), ("纯白", "trắng tinh"), ("绯红", "đỏ thắm"), ("青绿", "xanh ngọc"), ("草绿", "xanh lá"),
        ("天蓝", "xanh da trời"), ("钴蓝", "xanh coban"), ("芥黄", "vàng mù tạt"), ("荧黄", "vàng neon"),
        ("商", "Đời Thương"), ("汉", "Đời Hán"), ("魏晋", "Ngụy Tấn"), ("唐", "Đời Đường"),
        ("宋", "Đời Tống"), ("元", "Đời Nguyên"), ("明", "Đời Minh"), ("清", "Đời Thanh"), ("民国", "Dân Quốc"),
        ("・", " · "), ("·", " · "), ("（", " ("), ("）", ")")
    ];

    private static readonly string[] TopTokens =
        ["针织", "吊带", "衬衫", "女衫", "毛衣", "卫衣", "马甲", "开衫", "罩衫", "夹克", "大衣", "束腰胸衣", "文胸", "内衣套装"];
    private static readonly string[] BottomTokens =
        ["半身裙", "长裤", "牛仔裤", "短裤", "裙裤", "内裤", "内衣套装"];
    private static readonly string[] OnePieceTokens =
        ["连衣裙", "衬衫裙", "泳衣", "内衣套装"];
    private static readonly string[] ColorTokens =
        ["象牙白", "奶油色", "酒红色", "橄榄绿", "薄荷绿", "薰衣草紫", "藏青色", "卡其色", "白色", "黑色", "灰色", "米色", "棕色", "红色", "粉色", "橙色", "黄色", "绿色", "蓝色", "紫色", "金色", "银色", "牛仔"];

    private static int CountDistinctColorsInSegment(string segment)
    {
        var remaining = segment;
        var count = 0;
        foreach (var token in ColorTokens)
        {
            if (token == "牛仔") continue;
            if (remaining.Contains(token, StringComparison.Ordinal))
            {
                count++;
                remaining = remaining.Replace(token, " ", StringComparison.Ordinal);
            }
        }
        return count;
    }

    private static readonly Regex ConflictingWardrobeRegex = new(
        @"(?:,\s*)?(?:\b(?:wearing|dressed\s+in|in\s+an?\s+outfit\s+of)\s+[^,.;\n]{3,95}|(?:mặc|đang\s+mặc|khoác|trang\s+phục)\s+[^,.;\n]{3,110})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(150));

    public OutfitGalleryService(IConfiguration config, ILogger<OutfitGalleryService> logger)
    {
        _logger = logger;
        var configured = config["PoseGallery:RootPath"];
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            _rootDir = Path.GetFullPath(configured);
        }
        else
        {
            var candidates = new List<string>();
            var envHome = Environment.GetEnvironmentVariable("GULA_STUDIO_HOME");
            if (!string.IsNullOrWhiteSpace(envHome))
                candidates.Add(Path.Combine(envHome.Trim(), "NEWTOY", "ai-pose-gallery-zh"));

            var current = AppContext.BaseDirectory;
            for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                candidates.Add(Path.Combine(current, "NEWTOY", "ai-pose-gallery-zh"));
                current = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
            }
            candidates.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "NEWTOY", "ai-pose-gallery-zh")));
            _rootDir = candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "data", "costume-zh.json"))) ?? string.Empty;
        }
        _thumbsRoot = string.IsNullOrEmpty(_rootDir) ? string.Empty : Path.GetFullPath(Path.Combine(_rootDir, "thumbs")) + Path.DirectorySeparatorChar;
        _imgRoot = string.IsNullOrEmpty(_rootDir) ? string.Empty : Path.GetFullPath(Path.Combine(_rootDir, "img")) + Path.DirectorySeparatorChar;
    }

    public void Prewarm() => EnsureLoaded();

    public bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _entries.Count > 0;
        }
    }

    public bool OutfitExists(string outfitId)
    {
        if (string.IsNullOrWhiteSpace(outfitId)) return false;
        EnsureLoaded();
        return _byId.ContainsKey(outfitId.Trim());
    }

    public OutfitGalleryResponse Query(bool includeNsfw, string? gender, string? collection, string? category, string? query, int page, int pageSize)
    {
        EnsureLoaded();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 12, 120);
        var q = query?.Trim() ?? string.Empty;
        var genderFilter = gender?.Trim() ?? string.Empty;
        var colFilter = collection?.Trim() ?? string.Empty;
        var catFilter = category?.Trim() ?? string.Empty;

        var visible = includeNsfw ? _entries : _sfwEntries;
        var genderOptions = includeNsfw ? _allGenderOptions : _sfwGenderOptions;

        var genderScoped = string.IsNullOrWhiteSpace(genderFilter)
            ? visible
            : visible.Where(e => e.GenderKey.Equals(genderFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var collectionOptions = genderScoped
            .GroupBy(e => (e.CollectionKey, e.CollectionLabelVi))
            .Select(g => new PoseGalleryFilterOption(g.Key.CollectionKey, g.Key.CollectionLabelVi, g.Count()))
            .OrderBy(o => o.Key switch { "shisei_modern" => 1, "modern" => 2, "shisei_fantasy" => 3, _ => 9 })
            .ToList();

        var catBase = string.IsNullOrWhiteSpace(colFilter)
            ? genderScoped
            : genderScoped.Where(e => e.CollectionKey.Equals(colFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var categoryOptions = catBase
            .GroupBy(e => (e.CategoryKey, e.CategoryLabelVi, e.IsNsfw))
            .Select(g => new PoseGalleryFilterOption(g.Key.CategoryKey, g.Key.CategoryLabelVi, g.Count(), g.Key.IsNsfw))
            .OrderBy(o => CategoryReliabilityRank.TryGetValue(o.Key, out var r) ? r : 99)
            .ThenByDescending(o => o.Count)
            .ToList();

        var filtered = genderScoped.Where(e =>
        {
            if (!string.IsNullOrWhiteSpace(colFilter) && !e.CollectionKey.Equals(colFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(catFilter) && !e.CategoryKey.Equals(catFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            if (q.Length > 0 && !e.SearchBlob.Contains(q, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }).ToList();

        var skipLong = (long)(page - 1) * pageSize;
        var paged = skipLong >= filtered.Count
            ? []
            : filtered
                .Skip((int)skipLong)
                .Take(pageSize)
                .Select(ToDto)
                .ToList();

        return new OutfitGalleryResponse(
            TotalOutfits: visible.Count,
            FilteredOutfits: filtered.Count,
            Page: page,
            PageSize: pageSize,
            IncludeNsfw: includeNsfw,
            IsAvailable: _entries.Count > 0,
            Collections: collectionOptions,
            Categories: categoryOptions,
            Items: paged,
            Genders: genderOptions);
    }

    public bool TryResolveImageFile(string outfitId, string kind, string view, out string fullPath, out string contentType)
    {
        fullPath = string.Empty;
        contentType = "application/octet-stream";
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(outfitId) || !_byId.TryGetValue(outfitId.Trim(), out var entry))
            return false;

        var useThumb = !string.Equals(kind, "full", StringComparison.OrdinalIgnoreCase);
        var selectedView = entry.Views.FirstOrDefault(v => v.ViewKey.Equals(view, StringComparison.OrdinalIgnoreCase))
            ?? entry.Views.FirstOrDefault();
        if (selectedView is null) return false;

        var relPath = useThumb ? selectedView.ThumbRel : selectedView.FullRel;
        if (string.IsNullOrWhiteSpace(relPath)) relPath = selectedView.FullRel;
        if (string.IsNullOrWhiteSpace(relPath) || string.IsNullOrEmpty(_rootDir)) return false;

        var candidate = Path.GetFullPath(Path.Combine(_rootDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(_thumbsRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(_imgRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(candidate))
        {
            var fallback = Path.GetFullPath(Path.Combine(_rootDir, selectedView.FullRel.Replace('/', Path.DirectorySeparatorChar)));
            if ((!fallback.StartsWith(_thumbsRoot, StringComparison.OrdinalIgnoreCase) &&
                 !fallback.StartsWith(_imgRoot, StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(fallback))
                return false;
            candidate = fallback;
        }

        fullPath = candidate;
        contentType = Path.GetExtension(candidate).ToLowerInvariant() switch
        {
            ".webp" => "image/webp",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
        return true;
    }

    private static readonly Regex MultiCommaCleanupRegex = new(@"\s*,\s*,+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EnglishArticleRegex = new(@"\b(?:a|an|the|his|her)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MultiSpaceCleanupRegex = new(@"\s{2,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string BuildSynchronizedPrompt(string outfitId, string basePrompt)
    {
        var prompt = basePrompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outfitId)) return prompt;
        EnsureLoaded();
        if (!_byId.TryGetValue(outfitId.Trim(), out var entry))
            return prompt;

        if (!string.IsNullOrEmpty(prompt))
        {
            prompt = ConflictingWardrobeRegex.Replace(prompt, match =>
            {
                var text = match.Value;
                if (text.Contains("tóc", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("mắt", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("eyes", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("khuôn mặt", StringComparison.OrdinalIgnoreCase))
                    return text;
                return string.Empty;
            });
            prompt = MultiCommaCleanupRegex.Replace(prompt, ", ").Trim(' ', ',');
        }

        var outfitClause = entry.PromptEn;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            var subjectCue = entry.GenderKey == "male" ? "a stylish young man" : "a stylish young woman";
            return $"head-to-toe full-body fashion portrait of {subjectCue} with entire head and feet clearly visible, wearing exact outfit: {outfitClause}, crisp fabric detail, natural soft daylight";
        }
        if (prompt.Contains(outfitClause, StringComparison.OrdinalIgnoreCase))
            return prompt;

        return $"{prompt}, wearing exact outfit: {outfitClause}";
    }

    private HashSet<string> BuildExistingRelativeFilesSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_rootDir)) return set;
        foreach (var sub in new[] { "img", "thumbs" })
        {
            var dir = Path.Combine(_rootDir, sub);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var full in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(_rootDir, full).Replace('\\', '/');
                    set.Add(rel);
                }
            }
            catch
            {
                // Ignore inaccessible subfolder
            }
        }
        return set;
    }

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded)) return;
        lock (_loadLock)
        {
            if (Volatile.Read(ref _loaded)) return;
            try
            {
                if (string.IsNullOrWhiteSpace(_rootDir)) return;

                var existingFiles = BuildExistingRelativeFilesSet();
                var entries = new List<OutfitEntry>();
                var byId = new Dictionary<string, OutfitEntry>(StringComparer.OrdinalIgnoreCase);

                if (!LoadFullShiseiCostumes(entries, byId, existingFiles))
                    LoadLegacyShiseiCostumes(entries, byId, existingFiles);

                LoadMensLookbookCostumes(entries, byId, existingFiles);
                LoadModernCostumes(entries, byId, existingFiles);

                entries = entries
                    .OrderByDescending(e => e.QualityScore)
                    .ThenBy(e => CategoryReliabilityRank.TryGetValue(e.CategoryKey, out var r) ? r : 99)
                    .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                static List<PoseGalleryFilterOption> BuildGenders(List<OutfitEntry> es) => es
                    .GroupBy(e => (e.GenderKey, e.GenderLabelVi))
                    .Select(g => new PoseGalleryFilterOption(g.Key.GenderKey, g.Key.GenderLabelVi, g.Count()))
                    .OrderBy(o => GenderOrder.TryGetValue(o.Key, out var r) ? r : 99)
                    .ToList();

                var sfwEntries = entries.Where(e => !e.IsNsfw).ToList();

                _entries = entries;
                _sfwEntries = sfwEntries;
                _byId = byId;
                _allGenderOptions = BuildGenders(entries);
                _sfwGenderOptions = BuildGenders(sfwEntries);
                _logger.LogInformation("Đã nạp kho {Count} trang phục cục bộ ({Male} Nam, {Unisex} Unisex, {Female} Nữ) từ {Root}.",
                    entries.Count,
                    entries.Count(e => e.GenderKey == "male"),
                    entries.Count(e => e.GenderKey == "unisex"),
                    entries.Count(e => e.GenderKey == "female"),
                    _rootDir);
            }
            finally
            {
                Volatile.Write(ref _loaded, true);
            }
        }
    }

    private bool LoadFullShiseiCostumes(List<OutfitEntry> entries, Dictionary<string, OutfitEntry> byId, HashSet<string> existingFiles)
    {
        var jsonPath = Path.Combine(_rootDir, "data", "costumes-shisei-full.json");
        if (!File.Exists(jsonPath)) return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var arrEl = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("items", out var itemsProp) ? itemsProp : default;
            if (arrEl.ValueKind != JsonValueKind.Array) return false;

            var added = 0;
            foreach (var el in arrEl.EnumerateArray())
            {
                var rawCode = GetStr(el, "id");
                var catCode = GetStr(el, "cat");
                if (string.IsNullOrWhiteSpace(rawCode) || string.Equals(catCode, "AC", StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = $"shisei-{rawCode}";
                if (byId.ContainsKey(id)) continue;

                var views = new List<OutfitViewInternal>();
                if (el.TryGetProperty("views", out var viewsEl) && viewsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var vProp in viewsEl.EnumerateObject())
                    {
                        if (vProp.Value.ValueKind != JsonValueKind.String) continue;
                        var vKey = vProp.Name;
                        var fname = vProp.Value.GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(fname)) continue;
                        var fullRel = $"img/costumes/shisei/{fname}";
                        var thumbRel = $"thumbs/costumes/shisei/{fname}";
                        if (!existingFiles.Contains(fullRel) && !existingFiles.Contains(thumbRel))
                            continue;
                        var vLabel = ViewLabelMap.TryGetValue(vKey, out var mapped) ? mapped : vKey;
                        views.Add(new OutfitViewInternal(vKey, vLabel, thumbRel, fullRel));
                    }
                }
                if (views.Count == 0) continue;

                views = views.OrderBy(v =>
                {
                    var idx = Array.IndexOf(ShiseiViewOrder, v.ViewKey);
                    return idx < 0 ? 99 : idx;
                }).ToList();

                var genderRaw = GetStr(el, "gender");
                var (genderKey, genderVi) = genderRaw.ToUpperInvariant() switch
                {
                    "M" => ("male", "Nam"),
                    "U" => ("unisex", "Unisex"),
                    _ => ("female", "Nữ")
                };

                var rating = GetStr(el, "rating");
                var (catKey, catVi, colKey, colVi, catNsfw) = ShiseiCatMap.TryGetValue(catCode, out var mappedCat)
                    ? mappedCat
                    : ("shisei_md", "Thời trang CC0", "shisei_modern", "Hiện đại · Đa phong cách (CC0)", false);
                var isNsfw = catNsfw || string.Equals(rating, "suggestive", StringComparison.OrdinalIgnoreCase);

                var eraKey = GetStr(el, "era");
                var eraEn = GetStr(el, "era_en");
                var eraZh = GetStr(el, "era_zh");
                var eraVi = ShiseiEraMapVi.TryGetValue(eraKey, out var ev) ? ev : (!string.IsNullOrWhiteSpace(eraEn) ? eraEn : eraZh);

                var nameZh = GetStr(el, "name_zh");
                var nameEn = GetStr(el, "name_en");
                var descEn = GetStr(el, "desc_en").Trim();
                var keepEn = GetStr(el, "keep_en").Trim();
                var promptEn = string.IsNullOrWhiteSpace(keepEn) ? descEn : $"{descEn}. {keepEn}";
                if (string.IsNullOrWhiteSpace(promptEn)) promptEn = nameEn;

                var nameVi = BuildShiseiNameVi(nameZh, nameEn, eraVi, rawCode);
                var tagsVi = new List<string> { genderVi };
                if (!string.IsNullOrWhiteSpace(eraVi)) tagsVi.Add(eraVi);
                tagsVi.Add("4 góc");

                var (qualityScore, qualityBadge) = EvaluateShiseiQuality(catCode, promptEn, views.Count);
                var searchBlob = $"{id} {rawCode} {nameVi} {nameEn} {nameZh} {eraVi} {eraEn} {eraZh} {catVi} {colVi} {genderVi} {(genderKey == "male" ? "men male nam đồ nam" : genderKey == "unisex" ? "unisex nam nữ" : "women female nữ")} {promptEn} {qualityBadge}";

                var entry = new OutfitEntry(
                    Id: id,
                    Code: rawCode,
                    GenderKey: genderKey,
                    GenderLabelVi: genderVi,
                    CollectionKey: colKey,
                    CollectionLabelVi: colVi,
                    CategoryKey: catKey,
                    CategoryLabelVi: catVi,
                    NameVi: nameVi,
                    NameOriginal: !string.IsNullOrWhiteSpace(nameZh) ? nameZh : nameEn,
                    PromptEn: promptEn,
                    TagsVi: tagsVi,
                    IsNsfw: isNsfw,
                    QualityScore: qualityScore,
                    QualityBadge: qualityBadge,
                    SearchBlob: searchBlob,
                    Views: views);

                entries.Add(entry);
                byId[id] = entry;
                added++;
            }

            return added > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể nạp costumes-shisei-full.json từ {Root}.", _rootDir);
            return false;
        }
    }

    private void LoadLegacyShiseiCostumes(List<OutfitEntry> entries, Dictionary<string, OutfitEntry> byId, HashSet<string> existingFiles)
    {
        var jsonPath = Path.Combine(_rootDir, "data", "costume-shisei-zh.json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = GetStr(el, "id");
                if (string.IsNullOrWhiteSpace(id) || byId.ContainsKey(id)) continue;

                var views = new List<OutfitViewInternal>();
                if (el.TryGetProperty("views", out var viewsEl) && viewsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in viewsEl.EnumerateArray())
                    {
                        var vKey = GetStr(v, "id");
                        var vThumb = GetStr(v, "缩略图").TrimStart('/');
                        var vFull = GetStr(v, "原图").TrimStart('/');
                        if (string.IsNullOrWhiteSpace(vFull) || vFull.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!existingFiles.Contains(vFull))
                            continue;
                        var vLabel = ViewLabelMap.TryGetValue(vKey, out var mapped) ? mapped : vKey;
                        views.Add(new OutfitViewInternal(vKey, vLabel, string.IsNullOrWhiteSpace(vThumb) ? vFull : vThumb, vFull));
                    }
                }
                if (views.Count == 0) continue;

                views = views.OrderBy(v =>
                {
                    var idx = Array.IndexOf(ShiseiViewOrder, v.ViewKey);
                    return idx < 0 ? 99 : idx;
                }).ToList();

                var rawTags = new List<string>();
                if (el.TryGetProperty("标签", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tagsEl.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                            rawTags.Add(t.GetString()!);
                }
                var isMale = rawTags.Contains("男装", StringComparer.Ordinal);
                var genderKey = isMale ? "male" : "female";
                var genderVi = isMale ? "Nam" : "Nữ";
                var nameRaw = GetStr(el, "服装名称");
                var keywordsEn = GetStr(el, "关键词");
                var code = id.Replace("shisei-", "", StringComparison.OrdinalIgnoreCase);
                var tagsVi = rawTags.Select(t => TagMapVi.TryGetValue(t, out var vi) ? vi : t).Distinct().ToList();
                var nameVi = TranslateOutfitName(nameRaw, "Cổ trang Trung Hoa", code);
                var (qualityScore, qualityBadge) = EvaluateShiseiQuality("HC", keywordsEn, views.Count);

                var searchBlob = $"{id} {code} {nameVi} {nameRaw} {keywordsEn} Cổ trang Trung Hoa {genderVi} {string.Join(' ', tagsVi)} {qualityBadge}";
                var entry = new OutfitEntry(
                    Id: id, Code: code, GenderKey: genderKey, GenderLabelVi: genderVi,
                    CollectionKey: "shisei_fantasy", CollectionLabelVi: "Giả tưởng & Cổ trang (CC0)",
                    CategoryKey: "shisei_hc", CategoryLabelVi: "Cổ trang Trung Hoa",
                    NameVi: nameVi, NameOriginal: nameRaw, PromptEn: keywordsEn.Trim(),
                    TagsVi: tagsVi, IsNsfw: false, QualityScore: qualityScore, QualityBadge: qualityBadge,
                    SearchBlob: searchBlob, Views: views);
                entries.Add(entry);
                byId[id] = entry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể nạp costume-shisei-zh.json từ {Root}.", _rootDir);
        }
    }

    private static readonly (string En, string Vi)[] EnglishMenOutfitTranslations =
    [
        ("short sleeved shirt", "áo sơ mi ngắn tay"), ("short-sleeved shirt", "áo sơ mi ngắn tay"),
        ("long sleeved shirt", "áo sơ mi dài tay"), ("long-sleeved shirt", "áo sơ mi dài tay"),
        ("button down shirt", "áo sơ mi cài nút"), ("button-down shirt", "áo sơ mi cài nút"),
        ("leather jacket", "áo khoác da"), ("denim jacket", "áo khoác jean"), ("bomber jacket", "áo khoác bomber"),
        ("puffer jacket", "áo khoác phao"), ("trench coat", "áo măng tô trench"), ("winter coat", "áo khoác mùa đông"),
        ("suit jacket", "áo vest"), ("cargo pants", "quần túi hộp cargo"), ("chino shorts", "quần short kaki"),
        ("chino pants", "quần kaki chino"), ("dress pants", "quần âu"), ("sweatpants", "quần nỉ"),
        ("track pants", "quần thể thao"), ("tank top", "áo ba lỗ"), ("t-shirt", "áo thun"), ("tee", "áo thun"),
        ("polo shirt", "áo polo"), ("flannel shirt", "áo sơ mi flannel"), ("dress shirt", "áo sơ mi công sở"),
        ("sweatshirt", "áo nỉ sweatshirt"), ("hoodie", "áo hoodie"), ("cardigan", "áo cardigan"),
        ("turtleneck", "áo cổ lọ"), ("sweater", "áo len"), ("pullover", "áo len chui đầu"),
        ("blazer", "áo blazer"), ("overcoat", "áo măng tô"), ("parka", "áo parka"), ("jacket", "áo khoác"),
        ("coat", "áo khoác dài"), ("suit", "bộ vest"), ("vest", "áo gile"), ("shirt", "áo sơ mi"),
        ("jeans", "quần jean"), ("trousers", "quần tây"), ("pants", "quần dài"), ("shorts", "quần short"),
        ("joggers", "quần jogger"), ("sneakers", "giày sneaker"), ("boots", "giày bốt"), ("shoes", "giày"),
        ("light blue", "xanh nhạt"), ("dark blue", "xanh đậm"), ("dark green", "xanh rêu"), ("off-white", "trắng kem"),
        ("charcoal", "xám than"), ("burgundy", "đỏ rượu"), ("maroon", "đỏ đô"), ("navy", "xanh navy"),
        ("olive", "xanh ô-liu"), ("khaki", "kaki"), ("beige", "màu be"), ("camel", "nâu lạc đà"), ("cream", "màu kem"),
        ("black", "đen"), ("white", "trắng"), ("grey", "xám"), ("gray", "xám"), ("blue", "xanh dương"),
        ("green", "xanh lá"), ("brown", "nâu"), ("tan", "nâu nhạt"), ("red", "đỏ"), ("pink", "hồng"),
        ("yellow", "vàng"), ("orange", "cam"), ("purple", "tím"), ("denim", "denim"),
        ("striped", "kẻ sọc"), ("plaid", "kẻ caro"), ("checkered", "kẻ ô"), ("floral", "họa tiết hoa"),
        (" and ", " · "), (" with ", " · ")
    ];

    private static readonly Dictionary<string, string> EnglishMenOutfitMap =
        EnglishMenOutfitTranslations.ToDictionary(t => t.En, t => t.Vi, StringComparer.OrdinalIgnoreCase);

    private static readonly Regex EnglishMenOutfitCompiledRegex = new(
        string.Join("|", EnglishMenOutfitTranslations.OrderByDescending(t => t.En.Length).Select(t => Regex.Escape(t.En))),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private void LoadMensLookbookCostumes(List<OutfitEntry> entries, Dictionary<string, OutfitEntry> byId, HashSet<string> existingFiles)
    {
        var jsonPath = Path.Combine(_rootDir, "data", "costumes-mens-lookbook.json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var arrEl = doc.RootElement.TryGetProperty("items", out var itemsProp) ? itemsProp : doc.RootElement;
            if (arrEl.ValueKind != JsonValueKind.Array) return;

            foreach (var el in arrEl.EnumerateArray())
            {
                var id = GetStr(el, "id");
                if (string.IsNullOrWhiteSpace(id) || byId.ContainsKey(id)) continue;

                var fullRel = GetStr(el, "img").TrimStart('/');
                var thumbRel = GetStr(el, "thumb").TrimStart('/');
                if (string.IsNullOrWhiteSpace(fullRel)) continue;
                if (!existingFiles.Contains(fullRel) && !existingFiles.Contains(thumbRel))
                    continue;

                var catKey = GetStr(el, "cat_key");
                var catVi = GetStr(el, "cat_vi");
                var outfitEn = GetStr(el, "outfit_en").Trim();
                if (string.IsNullOrWhiteSpace(outfitEn)) continue;
                var isComplete = el.TryGetProperty("is_complete", out var icEl) && icEl.ValueKind == JsonValueKind.True;
                var colorCount = el.TryGetProperty("color_count", out var ccEl) && ccEl.ValueKind == JsonValueKind.Number ? ccEl.GetInt32() : 1;

                var score = isComplete
                    ? (colorCount >= 2 ? 95 : 92)
                    : (colorCount >= 1 ? 82 : 74);
                var badge = score >= 90 ? "★ Chuẩn nhất" : score >= 78 ? "Ổn định" : "Thiếu mô tả / Dễ lệch";

                var nameVi = TranslateEnglishMenOutfit(outfitEn, catVi, id);
                var tagsVi = new List<string> { "Nam", catVi.Split('·')[0].Trim(), isComplete ? "Đủ bộ" : "Phối rời" };
                var views = new List<OutfitViewInternal>
                {
                    new("front", "Chính diện", string.IsNullOrWhiteSpace(thumbRel) ? fullRel : thumbRel, fullRel)
                };
                var searchBlob = $"{id} {nameVi} {outfitEn} {catVi} Lookbook Nam Hiện đại men male nam đồ nam {string.Join(' ', tagsVi)} {badge}";

                var entry = new OutfitEntry(
                    Id: id,
                    Code: id,
                    GenderKey: "male",
                    GenderLabelVi: "Nam",
                    CollectionKey: "mens_lookbook",
                    CollectionLabelVi: "Lookbook Nam Hiện đại (MIT)",
                    CategoryKey: string.IsNullOrWhiteSpace(catKey) ? "men_casual" : catKey,
                    CategoryLabelVi: string.IsNullOrWhiteSpace(catVi) ? "Thời trang Nam" : catVi,
                    NameVi: nameVi,
                    NameOriginal: outfitEn,
                    PromptEn: outfitEn,
                    TagsVi: tagsVi,
                    IsNsfw: false,
                    QualityScore: score,
                    QualityBadge: badge,
                    SearchBlob: searchBlob,
                    Views: views);

                entries.Add(entry);
                byId[id] = entry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể nạp costumes-mens-lookbook.json từ {Root}.", _rootDir);
        }
    }

    private static string TranslateEnglishMenOutfit(string outfitEn, string catVi, string code)
    {
        if (string.IsNullOrWhiteSpace(outfitEn)) return $"{catVi} · {code}";
        var s = EnglishArticleRegex.Replace(outfitEn, "");
        s = EnglishMenOutfitCompiledRegex.Replace(s, m => EnglishMenOutfitMap.TryGetValue(m.Value, out var vi) ? vi : m.Value);
        s = MultiSpaceCleanupRegex.Replace(s, " ").Replace(" · · ", " · ").Trim(' ', '·', ',');
        if (s.Length > 0) s = char.ToUpperInvariant(s[0]) + s[1..];
        return string.IsNullOrWhiteSpace(s) ? $"{catVi} · {code}" : s;
    }

    private void LoadModernCostumes(List<OutfitEntry> entries, Dictionary<string, OutfitEntry> byId, HashSet<string> existingFiles)
    {
        var jsonPath = Path.Combine(_rootDir, "data", "costume-zh.json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = GetStr(el, "id");
                if (string.IsNullOrWhiteSpace(id) || byId.ContainsKey(id)) continue;

                var thumbRel = GetStr(el, "缩略图").TrimStart('/');
                var fullRel = GetStr(el, "原图").TrimStart('/');
                if (string.IsNullOrWhiteSpace(fullRel) || fullRel.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!existingFiles.Contains(fullRel))
                    continue;

                var catRaw = GetStr(el, "分类");
                var nameRaw = GetStr(el, "服装名称");
                var keywordsEn = GetStr(el, "关键词");
                var isSensitiveFlag = el.TryGetProperty("敏感", out var sEl) && sEl.ValueKind == JsonValueKind.True;

                var (catKey, catVi, catNsfw) = ModernCategoryMap.TryGetValue(catRaw, out var cm)
                    ? cm
                    : ("other", catRaw, false);
                var isNsfw = isSensitiveFlag || catNsfw;

                var rawTags = new List<string>();
                if (el.TryGetProperty("标签", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tagsEl.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                            rawTags.Add(t.GetString()!);
                }
                var tagsVi = rawTags.Select(t => TagMapVi.TryGetValue(t, out var vi) ? vi : t).Distinct().ToList();
                var nameVi = TranslateOutfitName(nameRaw, catVi, id);
                var promptEn = BuildModernOutfitPrompt(nameRaw, keywordsEn);
                var (qualityScore, qualityBadge) = EvaluateModernQuality(catKey, nameRaw, keywordsEn);

                var views = new List<OutfitViewInternal>
                {
                    new("front", "Chính diện", string.IsNullOrWhiteSpace(thumbRel) ? fullRel : thumbRel, fullRel)
                };
                var searchBlob = $"{id} {nameVi} {nameRaw} {keywordsEn} {catVi} Hiện đại Nữ female women {string.Join(' ', tagsVi)} {qualityBadge}";

                var entry = new OutfitEntry(
                    Id: id,
                    Code: id,
                    GenderKey: "female",
                    GenderLabelVi: "Nữ",
                    CollectionKey: "modern",
                    CollectionLabelVi: "Thời trang Nữ 4 mùa",
                    CategoryKey: catKey,
                    CategoryLabelVi: catVi,
                    NameVi: nameVi,
                    NameOriginal: nameRaw,
                    PromptEn: promptEn,
                    TagsVi: tagsVi,
                    IsNsfw: isNsfw,
                    QualityScore: qualityScore,
                    QualityBadge: qualityBadge,
                    SearchBlob: searchBlob,
                    Views: views);

                entries.Add(entry);
                byId[id] = entry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể nạp costume-zh.json từ {Root}.", _rootDir);
        }
    }

    private static string BuildModernOutfitPrompt(string nameZh, string keywordsEn)
    {
        var zhParts = nameZh.Split('・', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cleanZh = string.Join("、", zhParts);
        var cleanEn = keywordsEn.Trim();
        if (string.IsNullOrWhiteSpace(cleanZh)) return cleanEn;
        if (string.IsNullOrWhiteSpace(cleanEn)) return cleanZh;
        return $"{cleanZh} ({cleanEn})";
    }

    private static (int Score, string Badge) EvaluateModernQuality(string catKey, string nameZh, string keywordsEn)
    {
        var pieces = nameZh.Split('・', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasOnePiece = OnePieceTokens.Any(t => nameZh.Contains(t, StringComparison.Ordinal));
        var hasTop = TopTokens.Any(t => nameZh.Contains(t, StringComparison.Ordinal));
        var hasBottom = BottomTokens.Any(t => nameZh.Contains(t, StringComparison.Ordinal));
        var distinctColorCount = ColorTokens.Count(t => nameZh.Contains(t, StringComparison.Ordinal));

        var isComplete = hasOnePiece || (hasTop && hasBottom);
        if (!isComplete)
        {
            var partialScore = (hasTop || hasBottom) ? 58 : 48;
            return (partialScore, "Thiếu mô tả / Dễ lệch");
        }
        if (distinctColorCount == 0)
            return (68, "Thiếu màu / Dễ lệch");

        var hasDoubleColorPiece = pieces.Any(piece => CountDistinctColorsInSegment(piece) >= 2);
        var hasUncoloredTights = pieces.Any(piece =>
            (piece.Contains("连裤袜", StringComparison.Ordinal) || piece.Contains("长筒丝袜", StringComparison.Ordinal)) &&
            !ColorTokens.Any(c => piece.Contains(c, StringComparison.Ordinal)));

        var score = catKey switch
        {
            "autumn" => 96,
            "summer" => 96,
            "spring" => 94,
            "winter" => 94,
            "lingerie" => 96,
            "party" => 92,
            "swim" => 91,
            "sexy" => 91,
            _ => 88
        };

        if (hasDoubleColorPiece) score -= 12;
        if (hasUncoloredTights) score -= 6;
        if (pieces.Length >= 7) score -= 4;
        else if (pieces.Length == 6) score -= 1;
        if (!hasDoubleColorPiece && distinctColorCount >= 2 && pieces.Length is >= 2 and <= 5) score += 1;
        if (keywordsEn.Length < 18) score -= 5;

        score = Math.Clamp(score, 45, 97);
        var badge = score >= 90 ? "★ Chuẩn nhất" : score >= 78 ? "Ổn định" : "Dễ lệch";
        return (score, badge);
    }

    private static (int Score, string Badge) EvaluateShiseiQuality(string catCode, string promptEn, int viewCount)
    {
        // All Shisei CC0 entries include hand-crafted 60-word anatomical garment specs + 4 studio angles
        var score = catCode.ToUpperInvariant() switch
        {
            "MD" or "FM" or "HC" or "WE" => 97,
            "FN" or "SP" or "WK" or "WH" => 96,
            _ => 94
        };
        if (promptEn.Length < 80) score -= 4;
        if (viewCount < 4) score -= 2;
        score = Math.Clamp(score, 85, 97);
        return (score, score >= 90 ? "★ Chuẩn nhất" : "Ổn định");
    }

    private static string BuildShiseiNameVi(string nameZh, string nameEn, string eraVi, string code)
    {
        if (!string.IsNullOrWhiteSpace(nameZh))
        {
            var translated = TranslateOutfitName(nameZh, eraVi, code);
            var cjk = translated.Count(c => c is >= '\u4E00' and <= '\u9FFF');
            if (cjk == 0)
                return !string.IsNullOrWhiteSpace(eraVi) && !translated.Contains(eraVi, StringComparison.OrdinalIgnoreCase)
                    ? $"{eraVi} · {translated}"
                    : translated;
        }
        if (!string.IsNullOrWhiteSpace(nameEn))
            return !string.IsNullOrWhiteSpace(eraVi) ? $"{eraVi} · {nameEn}" : nameEn;
        return $"{eraVi} · {code}";
    }

    private static string TranslateOutfitName(string rawName, string categoryVi, string code)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return $"{categoryVi} · {code}";
        var s = rawName;
        foreach (var (zh, vi) in GarmentTranslations)
            s = s.Replace(zh, vi, StringComparison.Ordinal);

        s = MultiSpaceCleanupRegex.Replace(s, " ").Replace(" ·  ", " · ").Trim();
        if (s.Length > 0)
            s = char.ToUpperInvariant(s[0]) + s[1..];

        var cjkCount = s.Count(c => c is >= '\u4E00' and <= '\u9FFF');
        if (cjkCount <= 2) return s;
        return $"{categoryVi} · {code} ({s})";
    }

    private static string GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() ?? string.Empty : string.Empty;

    private static OutfitGalleryItemDto ToDto(OutfitEntry e) => new(
        e.Id,
        e.Code,
        e.CollectionKey,
        e.CollectionLabelVi,
        e.CategoryKey,
        e.CategoryLabelVi,
        e.NameVi,
        e.NameOriginal,
        e.PromptEn,
        e.TagsVi,
        e.IsNsfw,
        e.QualityScore,
        e.QualityBadge,
        $"/api/v1/studio/outfit-gallery/image/{Uri.EscapeDataString(e.Id)}?kind=thumb&view={Uri.EscapeDataString(e.Views[0].ViewKey)}",
        e.Views.Select(v => new OutfitGalleryViewDto(
            v.ViewKey,
            v.ViewLabelVi,
            $"/api/v1/studio/outfit-gallery/image/{Uri.EscapeDataString(e.Id)}?kind=thumb&view={Uri.EscapeDataString(v.ViewKey)}")).ToList(),
        e.GenderKey,
        e.GenderLabelVi);

    private sealed record OutfitViewInternal(string ViewKey, string ViewLabelVi, string ThumbRel, string FullRel);

    private sealed record OutfitEntry(
        string Id,
        string Code,
        string GenderKey,
        string GenderLabelVi,
        string CollectionKey,
        string CollectionLabelVi,
        string CategoryKey,
        string CategoryLabelVi,
        string NameVi,
        string NameOriginal,
        string PromptEn,
        IReadOnlyList<string> TagsVi,
        bool IsNsfw,
        int QualityScore,
        string QualityBadge,
        string SearchBlob,
        List<OutfitViewInternal> Views);
}
