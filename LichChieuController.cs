using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

[Authorize(Roles = "CinemaManager,Admin")]
public class LichChieuController : Controller
{
    private readonly ApplicationDbContext _db;
    public LichChieuController(ApplicationDbContext db) => _db = db;

    // ================= BM08 - Phiếu lập lịch chiếu phim =================
    [HttpGet]
    public async Task<IActionResult> LapLich()
    {
        ViewBag.DanhSachPhim = await _db.Phims.Where(p => !p.IsDeleted).ToListAsync();
        ViewBag.DanhSachPhong = await _db.PhongChieus.Where(p => !p.IsDeleted).ToListAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LapLich(string maPhim, string maPhong, DateOnly ngayChieu,
        TimeOnly gioBatDau, TimeOnly gioKetThuc, decimal giaVeThuong, decimal giaVeVip)
    {
        var phim = await _db.Phims.FindAsync(maPhim);
        if (phim == null) return NotFound();

        // QD09: phim C18 không được xếp lịch chiếu trước 22:00
        if (phim.PhanLoaiTuoi == PhanLoaiTuoi.C18 && gioBatDau < new TimeOnly(22, 0))
        {
            ViewBag.Loi = "Phim phân loại C18 không được xếp lịch chiếu trước 22:00 (QD09).";
            ViewBag.DanhSachPhim = await _db.Phims.Where(p => !p.IsDeleted).ToListAsync();
            ViewBag.DanhSachPhong = await _db.PhongChieus.Where(p => !p.IsDeleted).ToListAsync();
            return View();
        }

        // QD10: cách suất kế tiếp cùng phòng >= 15 phút (MIN_GAP_BETWEEN_SHOWS)
        var trungLich = await _db.LichChieus.AnyAsync(l =>
            l.MaPhong == maPhong && l.NgayChieu == ngayChieu && !l.IsDeleted &&
            gioBatDau < l.GioKetThuc.AddMinutes(15) && l.GioBatDau < gioKetThuc.AddMinutes(15));

        if (trungLich)
        {
            ViewBag.Loi = "Khung giờ này trùng hoặc quá sát (< 15 phút) với suất chiếu khác cùng phòng (QD10).";
            ViewBag.DanhSachPhim = await _db.Phims.Where(p => !p.IsDeleted).ToListAsync();
            ViewBag.DanhSachPhong = await _db.PhongChieus.Where(p => !p.IsDeleted).ToListAsync();
            return View();
        }

        _db.LichChieus.Add(new LichChieu
        {
            MaLichChieu = "LC" + Guid.NewGuid().ToString("N")[..8].ToUpper(),
            MaPhim = maPhim,
            MaPhong = maPhong,
            NgayChieu = ngayChieu,
            GioBatDau = gioBatDau,
            GioKetThuc = gioKetThuc,
            GiaVeThuong = giaVeThuong,
            GiaVeVip = giaVeVip,
            TrangThai = TrangThaiLichChieu.DangChieu
        });
        await _db.SaveChangesAsync();

        TempData["ThongBao"] = "Đã lập lịch chiếu thành công.";
        return RedirectToAction(nameof(LapLich));
    }
}
