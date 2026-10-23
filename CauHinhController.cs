using System.Security.Claims;
using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

// QD22: chỉ Admin hệ thống được sửa tham số hệ thống
[Authorize(Roles = "Admin")]
public class CauHinhController : Controller
{
    private readonly ApplicationDbContext _db;
    public CauHinhController(ApplicationDbContext db) => _db = db;
    private string MaNguoiDungHienTai => User.FindFirst(ClaimTypes.NameIdentifier)!.Value;

    // ================= BM19 - Phiếu cấu hình tham số hệ thống =================
    [HttpGet]
    public async Task<IActionResult> DanhSach()
    {
        var ds = await _db.CauHinhHeThongs.OrderBy(c => c.MaThamSo).ToListAsync();
        return View(ds);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CapNhat(string maThamSo, string giaTri)
    {
        var thamSo = await _db.CauHinhHeThongs.FindAsync(maThamSo);
        if (thamSo == null) return NotFound();

        var giaTriCu = thamSo.GiaTri;
        thamSo.GiaTri = giaTri;
        thamSo.MaNvThayDoi = MaNguoiDungHienTai;
        thamSo.NgayApDung = DateOnly.FromDateTime(DateTime.Now);

        // BM19 yêu cầu ghi LOG_CAU_HINH (bảng 18) mỗi lần đổi tham số - log bất biến, lưu 3 năm
        _db.LogCauHinhs.Add(new LogCauHinh
        {
            MaLog = "LOG" + Guid.NewGuid().ToString("N")[..8].ToUpper(),
            MaThamSo = maThamSo,
            GiaTriCu = giaTriCu,
            GiaTriMoi = giaTri,
            MaNvThucHien = MaNguoiDungHienTai,
            ThoiGian = DateTime.Now
        });

        await _db.SaveChangesAsync();
        TempData["ThongBao"] = $"Đã cập nhật tham số \"{thamSo.TenThamSo}\" = {giaTri}.";
        return RedirectToAction(nameof(DanhSach));
    }

    // Xem lịch sử thay đổi tham số (từ LOG_CAU_HINH)
    [HttpGet]
    public async Task<IActionResult> LichSu()
    {
        var logs = await _db.LogCauHinhs
            .Include(l => l.ThamSo)
            .Include(l => l.NguoiThucHien)
            .OrderByDescending(l => l.ThoiGian)
            .Take(100)
            .ToListAsync();
        return View(logs);
    }
}
