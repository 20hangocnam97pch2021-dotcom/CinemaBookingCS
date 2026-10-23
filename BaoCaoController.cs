using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

[Authorize(Roles = "CinemaManager,Admin")]
public class BaoCaoController : Controller
{
    private readonly ApplicationDbContext _db;
    public BaoCaoController(ApplicationDbContext db) => _db = db;

    public class DongBaoCao
    {
        public string TenPhim { get; set; } = default!;
        public int SoVeBan { get; set; }
        public decimal DoanhThu { get; set; }
    }

    // ================= BM10 - Báo cáo doanh thu bán vé =================
    [HttpGet]
    public async Task<IActionResult> DoanhThu(DateOnly? tuNgay, DateOnly? denNgay)
    {
        tuNgay ??= DateOnly.FromDateTime(DateTime.Now.AddMonths(-1));
        denNgay ??= DateOnly.FromDateTime(DateTime.Now);

        // Dùng LINQ aggregate (tương đương MongoDB aggregation pipeline trong thiết kế gốc)
        var ketQua = await _db.ChiTietDatVes
            .Where(c => c.DatVe!.TrangThai == TrangThaiDatVe.DaThanhToan
                     && c.LichChieu!.NgayChieu >= tuNgay && c.LichChieu.NgayChieu <= denNgay)
            .GroupBy(c => c.LichChieu!.Phim!.TenPhim)
            .Select(g => new DongBaoCao
            {
                TenPhim = g.Key,
                SoVeBan = g.Count(),
                DoanhThu = g.Sum(x => x.GiaVe)
            })
            .OrderByDescending(x => x.DoanhThu)
            .ToListAsync();

        ViewBag.TuNgay = tuNgay;
        ViewBag.DenNgay = denNgay;
        ViewBag.TongDoanhThu = ketQua.Sum(x => x.DoanhThu);
        return View(ketQua);
    }

    public class DongCongSuat
    {
        public string TenPhong { get; set; } = default!;
        public int TongSuatChieu { get; set; }
        public int TongGheSuatChieu { get; set; } // tổng ghế × số suất
        public int SoVeBan { get; set; }
        public double TyLeLapDay { get; set; } // %
        public decimal DoanhThu { get; set; }
    }

    // ================= BM13 - Báo cáo công suất phòng chiếu =================
    [HttpGet]
    public async Task<IActionResult> CongSuatPhong(DateOnly? tuNgay, DateOnly? denNgay)
    {
        tuNgay ??= DateOnly.FromDateTime(DateTime.Now.AddMonths(-1));
        denNgay ??= DateOnly.FromDateTime(DateTime.Now);

        var phongs = await _db.PhongChieus.Where(p => !p.IsDeleted).ToListAsync();
        var ketQua = new List<DongCongSuat>();

        foreach (var phong in phongs)
        {
            var suatChieus = await _db.LichChieus
                .Where(l => l.MaPhong == phong.MaPhong && l.NgayChieu >= tuNgay && l.NgayChieu <= denNgay && !l.IsDeleted)
                .ToListAsync();

            int tongSuat = suatChieus.Count;
            int tongGhe = tongSuat * phong.SucChua; // tổng chỗ ngồi × số suất
            var maLichChieus = suatChieus.Select(l => l.MaLichChieu).ToList();

            int soVeBan = await _db.ChiTietDatVes
                .CountAsync(c => maLichChieus.Contains(c.MaLichChieu) && c.DatVe!.TrangThai == TrangThaiDatVe.DaThanhToan);

            decimal doanhThu = await _db.ChiTietDatVes
                .Where(c => maLichChieus.Contains(c.MaLichChieu) && c.DatVe!.TrangThai == TrangThaiDatVe.DaThanhToan)
                .SumAsync(c => (decimal?)c.GiaVe) ?? 0;

            ketQua.Add(new DongCongSuat
            {
                TenPhong = phong.TenPhong,
                TongSuatChieu = tongSuat,
                TongGheSuatChieu = tongGhe,
                SoVeBan = soVeBan,
                TyLeLapDay = tongGhe > 0 ? Math.Round((double)soVeBan / tongGhe * 100, 1) : 0,
                DoanhThu = doanhThu
            });
        }

        ViewBag.TuNgay = tuNgay;
        ViewBag.DenNgay = denNgay;
        return View(ketQua);
    }
}
