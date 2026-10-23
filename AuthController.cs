using System.Security.Claims;
using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

public class AuthController : Controller
{
    private readonly ApplicationDbContext _db;
    public AuthController(ApplicationDbContext db) => _db = db;

    // ================= BM12 - Form đăng nhập hệ thống =================
    [HttpGet]
    public IActionResult DangNhap() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DangNhap(string email, string matKhau)
    {
        email = (email ?? "").Trim().ToLower();

        // Thử khách hàng trước, sau đó thử nhân viên (Direct Actor: Khách hàng / Nhân viên)
        var kh = await _db.KhachHangs.FirstOrDefaultAsync(x => x.Email == email && !x.IsDeleted);
        if (kh != null)
        {
            // QD15: sai 5 lần liên tiếp -> khoá 30 phút
            if (kh.KhoaDen.HasValue && kh.KhoaDen > DateTime.Now)
            {
                ViewBag.Loi = $"Tài khoản đang bị khoá đến {kh.KhoaDen:HH:mm dd/MM/yyyy}";
                return View();
            }
            if (BCrypt.Net.BCrypt.Verify(matKhau, kh.MatKhau))
            {
                kh.SoLanDangNhapSai = 0;
                await _db.SaveChangesAsync();
                await DangNhapThanhCong(kh.MaKh, kh.HoTen, "Khách hàng", null);
                return RedirectToAction("LichChieu", "Phim");
            }
            kh.SoLanDangNhapSai++;
            if (kh.SoLanDangNhapSai >= 5) // MAX_FAILED_LOGIN (tham số hệ thống)
                kh.KhoaDen = DateTime.Now.AddMinutes(30); // ACCOUNT_LOCK_MINUTES
            await _db.SaveChangesAsync();
            ViewBag.Loi = "Email hoặc mật khẩu không đúng";
            return View();
        }

        var nv = await _db.NhanViens.FirstOrDefaultAsync(x => x.Email == email);
        if (nv != null)
        {
            if (nv.TrangThai != TrangThaiNhanVien.HoatDong)
            {
                ViewBag.Loi = "Tài khoản nhân viên không ở trạng thái hoạt động";
                return View();
            }
            if (BCrypt.Net.BCrypt.Verify(matKhau, nv.MatKhau))
            {
                await DangNhapThanhCong(nv.MaNv, nv.HoTen, "NhanVien", nv.VaiTro.ToString());
                return RedirectToAction("LichChieu", "Phim");
            }
        }

        ViewBag.Loi = "Email hoặc mật khẩu không đúng";
        return View();
    }

    private async Task DangNhapThanhCong(string maNguoiDung, string hoTen, string loaiTaiKhoan, string? vaiTro)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, maNguoiDung),
            new(ClaimTypes.Name, hoTen),
            new("LoaiTaiKhoan", loaiTaiKhoan), // "Khách hàng" hoặc "NhanVien"
        };
        if (vaiTro != null) claims.Add(new Claim(ClaimTypes.Role, vaiTro));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    // ================= BM11 - Phiếu đăng ký tài khoản khách hàng =================
    [HttpGet]
    public IActionResult DangKy() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DangKy(string hoTen, string email, string matKhau, string xacNhanMk, string sdt, DateOnly? ngaySinh)
    {
        email = (email ?? "").Trim().ToLower();

        // QD14: mật khẩu >= 8 ký tự, có chữ hoa và số
        if (matKhau != xacNhanMk)
        {
            ViewBag.Loi = "Xác nhận MK không khớp";
            return View();
        }
        if (matKhau.Length < 8 || !matKhau.Any(char.IsUpper) || !matKhau.Any(char.IsDigit))
        {
            ViewBag.Loi = "Mật khẩu phải từ 8 ký tự trở lên, có ít nhất 1 chữ hoa và 1 chữ số";
            return View();
        }
        if (await _db.KhachHangs.AnyAsync(x => x.Email == email))
        {
            ViewBag.Loi = "Email đã được sử dụng";
            return View();
        }

        var kh = new KhachHang
        {
            MaKh = "KH" + Guid.NewGuid().ToString("N")[..8].ToUpper(),
            HoTen = hoTen,
            Email = email,
            MatKhau = BCrypt.Net.BCrypt.HashPassword(matKhau),
            Sdt = sdt,
            NgaySinh = ngaySinh.HasValue ? ngaySinh.Value.ToDateTime(TimeOnly.MinValue) : null,
            DaXacThucEmail = false // TODO: nối luồng gửi email xác thực thật (QD14)
        };
        _db.KhachHangs.Add(kh);
        await _db.SaveChangesAsync();

        TempData["ThongBao"] = "Đăng ký thành công, vui lòng đăng nhập.";
        return RedirectToAction(nameof(DangNhap));
    }

    [HttpPost]
    public async Task<IActionResult> DangXuat()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(DangNhap));
    }

    public IActionResult KhongCoQuyen() => View();
}
